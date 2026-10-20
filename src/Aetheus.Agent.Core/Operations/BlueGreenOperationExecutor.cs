// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The blue-green host deployment sequence, as four steps a pipeline composes itself.
///
/// Production, fast-release and nightly each re-implemented this in shell, and they drifted: one
/// omitted a frontend contract check the others enforced, and another let a response-time budget roll
/// back a deployment that was serving correctly. One implementation removes the drift, and splitting
/// it into four operations means a failure names what actually broke instead of reporting a single
/// opaque step.
///
/// The split is possible because state lives in <see cref="BlueGreenJournal"/> on disk rather than in
/// a lock held by one process. Each operation re-derives its context, reads what the previous step
/// committed, and refuses to act on a scope it cannot validate.
///
/// The migration path is expand/contract only: no backup is taken, because a contract that requires
/// every migration to be backward compatible is what makes the previous colour safe to keep serving.
/// A backup here would paper over a violated contract instead of surfacing it.
/// </summary>
public sealed class BlueGreenOperationExecutor(
    IShellRunner shell,
    IOptions<AetheusAgentOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<BlueGreenOperationExecutor> logger,
    TimeProvider? timeProvider = null) : EnvironmentOperationExecutor
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private readonly AetheusAgentOptions _options = options.Value;

    /// <summary>The one door to Compose; see <see cref="BlueGreenCompose"/> for why it is not inline.</summary>
    private readonly BlueGreenCompose _compose = new(shell);

    /// <summary>Names whoever holds a port the idle colour could not bind; see <see cref="BlueGreenPortHolders"/>.</summary>
    private readonly BlueGreenPortHolders _portHolders = new(shell);

    /// <summary>Closes an interrupted first deployment; see <see cref="BlueGreenInitialDeployment"/>.</summary>
    private readonly BlueGreenInitialDeployment _initialDeployment = new(shell);

    /// <summary>Returns traffic to the colour kept in reserve; see <see cref="BlueGreenRevert"/>.</summary>
    private readonly BlueGreenRevert _revert = new(shell);

    /// <summary>The expand/contract gate of the migrate step; see <see cref="BlueGreenMigrationGateCheck"/>.</summary>
    private readonly BlueGreenMigrationGateCheck _gate = new(shell);

    public override bool CanHandle(OperationKind kind) => kind
        is OperationKind.BlueGreenMigrate
        or OperationKind.BlueGreenUp
        or OperationKind.BlueGreenSwitch
        or OperationKind.BlueGreenCommit
        or OperationKind.BlueGreenRollback
        or OperationKind.BlueGreenRetire
        or OperationKind.BlueGreenRevert;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!CanHandle(kind)) return new ExecutorResult(1, false);
        if (!OperationTargetValidator.IsValid(kind, target))
        {
            await onOutput($"Invalid Compose project '{target}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (!BlueGreenContext.TryCreate(target, envVars, out var context, out var contextError))
        {
            // Use the journal to determine whether rollback is needed when context files are
            // absent. An unreachable open transaction must fail rather than claim successful
            // restoration.
            if (kind == OperationKind.BlueGreenRollback
                && BlueGreenJournal.HoldsNoOpenTransaction(envVars.GetValueOrDefault("AETHEUS_BG_STATE_DIR", string.Empty).Trim()))
            {
                await onOutput(
                    "No deployment transaction is open: this run never switched colour, nothing to roll back.",
                    TaskLogLevel.Info).ConfigureAwait(false);
                return new ExecutorResult(0, false);
            }
            await onOutput(contextError, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (!await DockerProbe.IsAvailableAsync(shell, ct).ConfigureAwait(false))
        {
            await onOutput(
                "Docker is not reachable for the agent user; install the agent with --enable-docker.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        // Serialise before reading any state. Every operation below derives what it does from the
        // live colour or the journal, so two concurrent runs would each act on a snapshot the other
        // is invalidating.
        if (!BlueGreenEnvironmentLease.TryAcquire(context!, out var lease, out var leaseError))
        {
            await onOutput(leaseError, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        using var held = lease!;
        var journal = new BlueGreenJournal(context!);

        return kind switch
        {
            OperationKind.BlueGreenMigrate => await MigrateAsync(context!, journal, envVars, timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            OperationKind.BlueGreenUp => await UpAsync(context!, journal, envVars, timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            OperationKind.BlueGreenSwitch => await SwitchAsync(context!, journal, envVars, timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            OperationKind.BlueGreenRollback => await RollbackAsync(context!, journal, envVars, timeoutSeconds, onOutput, ct, recoveringOrphan: false).ConfigureAwait(false),
            OperationKind.BlueGreenRetire => await RetireAsync(context!, journal, envVars, timeoutSeconds, onOutput, ct).ConfigureAwait(false),
            OperationKind.BlueGreenRevert => await _revert.RevertAsync(
                context!, journal, envVars, timeoutSeconds,
                (colour, budget) => WaitForColourAsync(context!, colour, (int)budget.TotalSeconds, onOutput, ct),
                onOutput, ct).ConfigureAwait(false),
            _ => await CommitAsync(context!, journal, timeoutSeconds, onOutput, ct).ConfigureAwait(false)
        };
    }

    // ── Migrate ─────────────────────────────────────────────────────────────────────────────────
    private async Task<ExecutorResult> MigrateAsync(
        BlueGreenContext context, BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var up = await _compose.RunAsync(context, ["up", "-d", "--wait", "--no-build", "database"], timeoutSeconds, ct)
            .ConfigureAwait(false);
        if (up.ExitCode != 0)
        {
            await onOutput($"Shared database did not come up: {up.StdErr}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var target = journal.ReadLiveColour() is { } live ? BlueGreenContext.Opposite(live) : "blue";

        // Expand/contract gate: see BlueGreenMigrationGateCheck. It also publishes its verdict for the run page.
        if (await _gate.RunAsync(context, envVars, timeoutSeconds, onOutput, ct).ConfigureAwait(false) is { } refused)
            return refused;

        await onOutput($"Running the migration bundle once against the shared database ({target} job)…", TaskLogLevel.Info)
            .ConfigureAwait(false);

        // A dedicated one-shot job, never the application replicas: both colours set
        // Database__SkipMigrations so they cannot race each other on the shared schema.
        var migrate = await _compose.RunAsync(
            context,
            [
                "--profile", target, "run", "--rm", "--no-deps",
                "-e", "AETHEUS_RUN_MIGRATIONS=true",
                "-e", "AETHEUS_MIGRATE_ONLY=true",
                $"back-{target}"
            ],
            timeoutSeconds, ct).ConfigureAwait(false);
        if (migrate.StdOut.Length > 0) await onOutput(migrate.StdOut, TaskLogLevel.Info).ConfigureAwait(false);
        if (migrate.ExitCode != 0)
        {
            await onOutput($"Migration job failed: {migrate.StdErr}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        await onOutput("Schema reconciled; both colours can serve it.", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    // ── Up ──────────────────────────────────────────────────────────────────────────────────────
    private async Task<ExecutorResult> UpAsync(
        BlueGreenContext context, BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        // Before anything is rebuilt: the idle colour may be what another run's open transaction falls back to.
        if (await BlueGreenTransactionOwnership.SettleAnotherRunsTransactionAsync(
                journal, envVars, () => RollbackAsync(context, journal, envVars, timeoutSeconds, onOutput, ct, recoveringOrphan: true),
                onOutput).ConfigureAwait(false) is { } refused)
            return refused;

        var live = journal.ReadLiveColour();
        var idle = live is null ? "blue" : BlueGreenContext.Opposite(live);
        await onOutput(
            live is null
                ? $"No colour is recorded as live; preparing {idle} as the initial deployment."
                : $"Live colour is {live}; preparing {idle}.",
            TaskLogLevel.Info).ConfigureAwait(false);

        // Rebuilding the idle colour invalidates its reserve record before startup, because it can
        // no longer identify the preceding deployment.
        var reserve = new BlueGreenReserve(context);
        if (reserve.Colour == idle)
        {
            reserve.Clear();
            await onOutput($"The {idle} colour kept in reserve (N-1) is recycled for this deployment.", TaskLogLevel.Info)
                .ConfigureAwait(false);
        }

        // Clear the idle colour before rebuilding it: a container left behind by an earlier failed
        // deployment keeps its published ports, and Compose cannot bind them again. Non-fatal, since
        // there is usually nothing to remove and `up` reports the real problem if there is.
        await _compose.ResetIdleColourAsync(context, idle, timeoutSeconds, ct).ConfigureAwait(false);

        var up = await _compose.RunAsync(
            context, ["--profile", idle, "up", "-d", "--wait", "--no-build"], timeoutSeconds, ct)
            .ConfigureAwait(false);
        if (up.ExitCode != 0)
        {
            await onOutput($"Idle colour {idle} failed to start: {up.StdErr}", TaskLogLevel.Error).ConfigureAwait(false);
            if (up.StdErr?.Contains("already allocated", StringComparison.OrdinalIgnoreCase) == true)
                await _portHolders.ReportAsync(context, idle, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            await _compose.DumpColourLogsAsync(context, idle, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            await _compose.StopColourAsync(context, idle, timeoutSeconds, ct).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        if (!await WaitForColourAsync(context, idle, timeoutSeconds, onOutput, ct).ConfigureAwait(false))
        {
            await onOutput(
                $"Idle colour {idle} never became ready; the live colour was left serving.",
                TaskLogLevel.Error).ConfigureAwait(false);
            await _compose.DumpColourLogsAsync(context, idle, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            await _compose.StopColourAsync(context, idle, timeoutSeconds, ct).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        await onOutput($"##aetheus[setvariable name=BLUEGREEN_IDLE_COLOR]{idle}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"##aetheus[setvariable name=BLUEGREEN_LIVE_COLOR]{live ?? "none"}", TaskLogLevel.Info).ConfigureAwait(false);
        var (front, back) = context.PortsFor(idle);
        await onOutput($"##aetheus[setvariable name=BLUEGREEN_IDLE_FRONT_PORT]{front}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"##aetheus[setvariable name=BLUEGREEN_IDLE_BACK_PORT]{back}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"Idle colour {idle} is ready on {front}/{back}.", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    // ── Switch ──────────────────────────────────────────────────────────────────────────────────
    private async Task<ExecutorResult> SwitchAsync(
        BlueGreenContext context, BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var live = journal.ReadLiveColour();
        var idle = live is null ? "blue" : BlueGreenContext.Opposite(live);
        if (BlueGreenSwitchInputs.Read(envVars, out var refusal) is not { } inputs)
        {
            await onOutput(refusal, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        var (revision, reloadCommand, templatePath, confPath) = inputs;

        if (!journal.TryOpen(live ?? "none", idle, revision, BlueGreenTransactionOwnership.OwnerOf(envVars), out var journalError))
        {
            await onOutput(journalError, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        // Snapshot before overwriting: a rejected reload must be able to put back exactly what was
        // serving, and a later step must be able to reconcile an interrupted switch.
        var previousConf = File.Exists(confPath) ? await File.ReadAllTextAsync(confPath, ct).ConfigureAwait(false) : null;
        if (previousConf is not null)
            await File.WriteAllTextAsync(Path.Combine(context.JournalDir, "upstream.before"), previousConf, ct).ConfigureAwait(false);

        var (front, back) = context.PortsFor(idle);
        var rendered = BlueGreenUpstream.Render(
            await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false), idle, front, back);
        if (rendered is null)
        {
            await onOutput(
                "Upstream template still contains unresolved placeholders after rendering.", TaskLogLevel.Error)
                .ConfigureAwait(false);
            journal.Clear();
            return new ExecutorResult(1, false);
        }
        await BlueGreenUpstream.WriteAtomicAsync(confPath, rendered, ct).ConfigureAwait(false);
        await onOutput($"Upstream configuration now points at {idle} ({front}/{back}).", TaskLogLevel.Info).ConfigureAwait(false);

        // The reload is self-validating: a rejected configuration fails the reload while the old
        // workers keep serving, so a bad switch cannot take the site down.
        var reload = await shell.RunExecAsync("sudo", ["-n", reloadCommand], ct, TimeSpan.FromSeconds(Math.Min(120, timeoutSeconds)))
            .ConfigureAwait(false);
        if (reload.ExitCode != 0)
        {
            await RestoreAfterRejectedReloadAsync(
                context, journal, confPath, previousConf, reloadCommand, reload.StdErr, timeoutSeconds, onOutput, ct)
                .ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        // Mark the journal before the live colour: a crash between the two must leave a transaction a
        // later step can still reconcile. The other order would record traffic as moved while the
        // journal still read PREPARED, and rollback would answer "nothing to undo" on a real cutover.
        journal.MarkSwitched();
        journal.WriteLiveColour(idle);
        await BlueGreenConfirmation.ArmIfRequestedAsync(
            _options.WorkDirectory, context.Project, revision, live, envVars, _time.GetUtcNow().UtcDateTime, onOutput)
            .ConfigureAwait(false);
        await onOutput($"##aetheus[setvariable name=BLUEGREEN_SWITCHED_TO]{idle}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"Traffic now points at {idle}. The previous colour is still running for rollback.", TaskLogLevel.Info)
            .ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    // The reload refused the rendered configuration: put back what was serving. Announcing a restore
    // without checking it would be the worst kind of lie here: the site would be down while the step
    // claims it put the old configuration back. Keep the journal when the restore also fails, since it
    // is the only record left to reconcile from.
    private async Task RestoreAfterRejectedReloadAsync(
        BlueGreenContext context, BlueGreenJournal journal, string confPath, string? previousConf, string reloadCommand,
        string reloadError, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        await onOutput(
            $"Reload rejected the new configuration; restoring the previous one: {reloadError}",
            TaskLogLevel.Error).ConfigureAwait(false);
        if (!await BlueGreenUpstream.RestoreAsync(shell, confPath, previousConf, reloadCommand, timeoutSeconds, ct).ConfigureAwait(false))
        {
            await onOutput(
                $"Restoring the previous configuration ALSO failed; the site may be down and manual intervention is required. Journal retained at {context.JournalDir}.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return;
        }
        journal.Clear();
    }

    // ── Commit ──────────────────────────────────────────────────────────────────────────────────
    private async Task<ExecutorResult> CommitAsync(
        BlueGreenContext context, BlueGreenJournal journal, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!journal.Exists || journal.State != BlueGreenJournal.Switched)
        {
            await onOutput(
                $"No switched transaction to commit (state={journal.State ?? "none"}).", TaskLogLevel.Error)
                .ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var previous = journal.PreviousLive;
        var revision = journal.Revision ?? string.Empty;

        // Keep the replaced colour running as the reserve.
        await new BlueGreenReserve(context).KeepPreviousAsync(journal, onOutput, ct).ConfigureAwait(false);

        if (revision.Length > 0) journal.WriteDeployedRevision(revision);
        journal.Commit();
        BlueGreenConfirmation.Disarm(_options.WorkDirectory, context.Project);
        await onOutput($"Deployment committed{(revision.Length > 0 ? $" at {revision}" : string.Empty)}.", TaskLogLevel.Info)
            .ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    // ── Rollback ────────────────────────────────────────────────────────────────────────────────
    /// <summary>Restore traffic to the colour serving before the switch, including when the journal
    /// is still PREPARED. Traffic can already have moved if interruption occurs between reload and
    /// recording SWITCHED.</summary>
    // ── Retire ──────────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Undoes a FIRST deployment that already moved traffic, which a rollback cannot: there is no
    /// previous colour to put back, so <see cref="BlueGreenInitialDeployment"/> refuses that case
    /// rather than taking the site down under the name of a rollback. Refusing was right; leaving no
    /// way forward was not. The journal then rejects every later deployment ("resolve it before
    /// starting another"), and until this operation existed nothing could resolve it but hand surgery
    /// on a live host.
    ///
    /// The inverse case is refused here for the same reason, in the other direction: a transaction
    /// with a previous colour is a rollback, and retiring it would stop a colour that has somewhere
    /// to return to.
    ///
    /// The shared database and the environment file are deliberately untouched. Secret-zero cannot be
    /// regenerated, and the schema is shared with the colour a later deployment will bring up.
    /// </summary>
    private async Task<ExecutorResult> RetireAsync(
        BlueGreenContext context, BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var state = journal.Exists ? journal.State : null;
        if (state is null or BlueGreenJournal.Committed)
        {
            // Without an open transaction, retirement stops only the reserve, never the live
            // colour.
            if (await new BlueGreenReserve(context).RetireAsync(journal, _compose, timeoutSeconds, onOutput, ct).ConfigureAwait(false) is { } retiredReserve)
                return retiredReserve;
            await onOutput("No open deployment transaction and no colour in reserve; nothing to retire.", TaskLogLevel.Info).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }

        var previous = journal.PreviousLive;
        if (previous is "blue" or "green")
        {
            await onOutput(
                $"This transaction has a previous colour ({previous}), so it can be rolled back. Retiring it would "
                + "stop a deployment that has somewhere to return to; use bluegreen-rollback instead.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        if (!BlueGreenUpstream.TryReadTargets(envVars, out var confPath, out var reloadCommand))
        {
            await onOutput(
                "AETHEUS_BG_UPSTREAM_CONF and AETHEUS_BG_RELOAD_HELPER are required to retire a deployment.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        // Order matters: move traffic off the colour before stopping it, so the window in which the
        // upstream points at a dead port is as short as the reload itself.
        if (!await BlueGreenUpstream.RestoreRecordedOrReportAsync(
                shell, context, confPath, reloadCommand, timeoutSeconds, onOutput, ct).ConfigureAwait(false))
            return new ExecutorResult(1, false);

        var retired = journal.Idle;
        if (retired is "blue" or "green"
            && await _compose.StopColourAsync(context, retired, timeoutSeconds, ct).ConfigureAwait(false) != 0)
        {
            // Keep the transaction: it is the only record that this colour still has to be stopped.
            await onOutput(
                $"The {retired} colour could not be stopped; the deployment transaction stays open so the retirement can be retried.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        journal.ClearLiveColour();
        journal.Clear();
        await onOutput("##aetheus[setvariable name=BLUEGREEN_RETIRED]true", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput(
            $"Initial deployment retired: the recorded upstream configuration is back, colour {retired ?? "none"} is "
            + "stopped and the transaction is closed. The shared database and the environment file are untouched.",
            TaskLogLevel.Warning).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    private async Task<ExecutorResult> RollbackAsync(
        BlueGreenContext context, BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct, bool recoveringOrphan)
    {
        var state = journal.Exists ? journal.State : null;
        if (await RefuseRollbackAsync(context, journal, state, envVars, onOutput, recoveringOrphan).ConfigureAwait(false) is { } refused)
            return refused;

        if (!BlueGreenUpstream.TryReadTargets(envVars, out var confPath, out var reloadCommand))
        {
            await onOutput(
                "AETHEUS_BG_UPSTREAM_CONF and AETHEUS_BG_RELOAD_HELPER are required to restore traffic.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (state == BlueGreenJournal.Prepared)
        {
            await onOutput(
                "The deployment transaction was interrupted before the switch was recorded; traffic may already have "
                + "moved, so the recorded configuration is restored rather than assumed intact.",
                TaskLogLevel.Warning).ConfigureAwait(false);
        }

        var previous = journal.PreviousLive;
        if (previous is not ("blue" or "green"))
        {
            return await _initialDeployment.ReconcileAsync(
                context, journal, state!, confPath, reloadCommand, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        }
        return await RestorePreviousColourAsync(
            context, journal, previous, confPath, reloadCommand, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
    }

    /// <summary>The result that ends the rollback before anything is touched: nothing open to undo
    /// (success), another run's transaction, or a state nobody wrote; <c>null</c> to go on.</summary>
    private static async Task<ExecutorResult?> RefuseRollbackAsync(
        BlueGreenContext context, BlueGreenJournal journal, string? state, IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput, bool recoveringOrphan)
    {
        if (state is null or BlueGreenJournal.Committed)
        {
            await onOutput("No open deployment transaction; nothing to undo.", TaskLogLevel.Info).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }
        // A failed run undoes its own deployment, never another run's (decision of 2026-10-02).
        if (!recoveringOrphan
            && await BlueGreenTransactionOwnership.RefuseAnotherRunsRollbackAsync(journal, envVars, onOutput).ConfigureAwait(false) is { } notOurs)
            return notOurs;
        if (state is BlueGreenJournal.Prepared or BlueGreenJournal.Switched)
            return null;
        await onOutput(
            $"The deployment transaction is in an unrecognised state ('{state}'); refusing to guess what to restore. "
            + $"Inspect {context.JournalDir} and resolve it by hand.",
            TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(1, false);
    }

    private async Task<ExecutorResult> RestorePreviousColourAsync(
        BlueGreenContext context, BlueGreenJournal journal, string previous, string confPath, string reloadCommand,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        // The colour was stopped only at commit, which has not run, so it should still be up. Start
        // it anyway and health-gate it: restoring traffic to a colour that cannot serve would turn a
        // failed deployment into an outage.
        await _compose.RunAsync(
            context, ["--profile", previous, "start", $"back-{previous}", $"front-{previous}"], timeoutSeconds, ct)
            .ConfigureAwait(false);
        if (!await WaitForColourAsync(context, previous, timeoutSeconds, onOutput, ct).ConfigureAwait(false))
        {
            await onOutput(
                $"The previous {previous} colour did not become ready; traffic was NOT moved back. Manual intervention "
                + $"required. Journal retained at {context.JournalDir}.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        if (!await BlueGreenUpstream.RestoreRecordedOrReportAsync(
                shell, context, confPath, reloadCommand, timeoutSeconds, onOutput, ct).ConfigureAwait(false))
            return new ExecutorResult(1, false);

        journal.WriteLiveColour(previous);
        await _compose.StopColourAsync(context, BlueGreenContext.Opposite(previous), timeoutSeconds, ct).ConfigureAwait(false);
        journal.Clear();
        BlueGreenConfirmation.Disarm(_options.WorkDirectory, context.Project);
        await onOutput("##aetheus[setvariable name=BLUEGREEN_ROLLED_BACK]true", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"Traffic restored to {previous}.", TaskLogLevel.Warning).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    /// <summary>
    /// There was no colour serving before this transaction. An interrupted first deployment still has
    /// to be undone - the upstream file may already point at a colour nobody validated - but a
    /// completed one cannot be rolled back to anything, so it is refused instead of taking the site
    /// down under the name of a rollback.
    /// </summary>

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────
    // Upstream rendering, atomic write and restore live in BlueGreenUpstream.

    private async Task<bool> WaitForColourAsync(
        BlueGreenContext context, string colour, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(SmokeOperationExecutor.DeploymentProbeHttpClientName);
        return await BlueGreenReadiness
            .WaitAsync(client, context, colour, TimeSpan.FromSeconds(timeoutSeconds), logger, onOutput, ct)
            .ConfigureAwait(false);
    }

}
