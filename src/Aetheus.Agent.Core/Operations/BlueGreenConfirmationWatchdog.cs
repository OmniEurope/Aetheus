// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>Revert only an unconfirmed SWITCHED transaction matching the armed revision when its
/// host-side deadline expires; disarm stale watches otherwise. Record the outcome in auto-reverted
/// and the agent log independently of backend availability.</summary>
public sealed class BlueGreenConfirmationWatchdog(
    IShellRunner shell,
    IOptions<AetheusAgentOptions> options,
    TimeProvider timeProvider,
    ILogger<BlueGreenConfirmationWatchdog> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly BlueGreenCompose _compose = new(shell);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Blue-green confirmation sweep failed; retrying");
            }
            await Task.Delay(Interval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One pass over the armed watches; returns how many deployments it put back.</summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        var reverted = 0;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var (path, watch) in BlueGreenConfirmation.ReadAll(options.Value.WorkDirectory).ToList())
        {
            if (watch is null)
            {
                logger.LogError("Unreadable blue-green confirmation watch {Path}; left in place for inspection", path);
                continue;
            }
            if (watch.DeadlineUtc > now) continue;
            var outcome = await RevertAsync(watch, ct).ConfigureAwait(false);
            if (outcome == Outcome.Retry) continue;
            if (outcome == Outcome.Reverted) reverted++;
            BlueGreenConfirmation.Disarm(options.Value.WorkDirectory, watch.Project);
        }
        return reverted;
    }

    internal enum Outcome { Reverted, NothingToUndo, Failed, Retry }

    private async Task<Outcome> RevertAsync(BlueGreenConfirmation.Watch watch, CancellationToken ct)
    {
        if (!BlueGreenContext.TryCreateForRecovery(watch.Project, watch.Variables, out var context, out var error))
        {
            logger.LogError("Confirmation window of {Project} ran out, but its environment is unusable: {Error}", watch.Project, error);
            return Outcome.Failed;
        }
        if (!BlueGreenEnvironmentLease.TryAcquire(context!, out var lease, out var leaseError))
        {
            // Something is acting on the environment right now (the pipeline's own Rollback, a
            // commit); retried on the next pass rather than raced.
            logger.LogWarning("Confirmation window of {Project} ran out while another operation holds it: {Error}", watch.Project, leaseError);
            return Outcome.Retry;
        }
        using var held = lease!;
        var journal = new BlueGreenJournal(context!);
        if (journal.State != BlueGreenJournal.Switched || journal.Revision != watch.Revision
            || journal.PreviousLive is not ("blue" or "green"))
        {
            logger.LogInformation(
                "Confirmation window of {Project} ran out, but the transaction is no longer the switched {Revision}; nothing to undo",
                watch.Project, watch.Revision);
            return Outcome.NothingToUndo;
        }

        var previous = journal.PreviousLive!;
        var confPath = watch.Variables.GetValueOrDefault("AETHEUS_BG_UPSTREAM_CONF", string.Empty).Trim();
        var reloadCommand = watch.Variables.GetValueOrDefault("AETHEUS_BG_RELOAD_HELPER", string.Empty).Trim();
        var timeoutSeconds = options.Value.MaxTimeoutSeconds;
        if (confPath.Length == 0 || reloadCommand.Length == 0
            || !await BlueGreenUpstream.RestoreRecordedAsync(shell, context!, confPath, reloadCommand, timeoutSeconds, ct).ConfigureAwait(false))
        {
            logger.LogCritical(
                "Confirmation window of {Project} ran out and restoring the previous configuration FAILED; journal kept at {Journal}",
                watch.Project, context!.JournalDir);
            return Outcome.Failed;
        }

        var unconfirmed = journal.Idle;
        journal.WriteLiveColour(previous);
        journal.Clear();
        var stopped = unconfirmed is "blue" or "green"
            && await _compose.StopColourAsync(context!, unconfirmed, timeoutSeconds, ct).ConfigureAwait(false) == 0;
        await File.WriteAllTextAsync(
            Path.Combine(context!.StateDir, "auto-reverted"),
            $"at={timeProvider.GetUtcNow().UtcDateTime:O}\nrevision={watch.Revision}\nback_to={previous}\nunconfirmed_stopped={stopped}\n",
            ct).ConfigureAwait(false);
        logger.LogWarning(
            "Deployment {Revision} of {Project} was not confirmed in time: traffic is back on {Previous}; the unconfirmed colour {Unconfirmed} {Stopped}",
            watch.Revision, watch.Project, previous, unconfirmed, stopped ? "is stopped" : "could not be stopped");
        return Outcome.Reverted;
    }
}
