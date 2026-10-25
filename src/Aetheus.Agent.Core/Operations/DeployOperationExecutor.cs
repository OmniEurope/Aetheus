// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Cross-agent deployment (<see cref="OperationKind.PipelineDeploy"/>). Pulls the resolved build
/// artifact from the backend's IDOR-safe agent endpoint, then applies it on this host:
/// <list type="bullet">
/// <item><b>binary</b> - unzip into a fresh <c>$BASE/&lt;app&gt;/releases/&lt;runId&gt;</c> dir (never the
/// live one), locate the <c>run</c> launcher, flip the <c>current</c> symlink atomically (ln + <c>mv -T</c>)
/// onto its directory, restart via the root-owned <c>deploy-restart</c> helper, then health-gate on the
/// unit's <c>ActiveState</c>/<c>SubState</c>/<c>NRestarts</c> (crash-loop aware, not a single is-active);
/// on failure roll <c>current</c> back to the previous release, restart, and report the rollback's own
/// outcome honestly.</item>
/// <item><b>container</b> - verify the docker daemon is reachable, snapshot the previously-tagged image
/// ids, <c>docker load</c> the saved tarball(s), then <c>docker compose up -d --wait</c> (the <c>--wait</c>
/// exit code IS the health gate); on failure re-tag the previous images and bring the old stack back.</item>
/// </list>
/// Everything is argv-only (no shell, no interpolation). The app name and compose path are
/// re-validated here as a last-line defence before any privileged action. Linux only - Windows
/// deploy (no unit template) is a v2 concern.
/// </summary>
public sealed class DeployOperationExecutor(
    IServerApiClient apiClient,
    IShellRunner shell,
    IOptions<AetheusAgentOptions> options,
    ILogger<DeployOperationExecutor> logger) : EnvironmentOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    // Must match install-agent-linux.sh: DEPLOY_BASE_DIR + the root-owned restart helper path.
    // A guard test (DeployPathConstantsAuditTests) keeps these in lockstep with the install script.
    internal const string DeployBaseDir = "/var/lib/aetheus-agent/deploy";
    internal const string RestartHelperPath = "/usr/local/lib/aetheus/deploy-restart";
    // systemd unit template instantiated per app (aetheus-app@<app>.service). Also hard-coded
    // in install-agent-linux.sh (DEPLOY_UNIT_TEMPLATE_PATH) with no compile-time link - a rename
    // breaks the health-gate at deploy time, so DeployPathConstantsAuditTests pins it to the script.
    internal const string UnitTemplatePrefix = "aetheus-app@";

    // Health-gate cadence: a continuous running dwell of RequiredStableSamples×PollSeconds (8s) must
    // exceed the unit's RestartSec (5s in the template) so a warmup-then-crash loop can't false-pass.
    private const int PollSeconds = 2;
    private const int RequiredStableSamples = 4;

    // Replaceable so the whole deploy runs in a test on any platform, under a temporary deploy root and
    // without real waits. The defaults are the production values above.
    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    internal string DeployRoot { get; init; } = DeployBaseDir;
    internal TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromSeconds(PollSeconds);
    internal TimeSpan LockRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    // The readiness probe's transport: no redirect, no proxy. A test answers through its own handler instead
    // of a listening socket.
    internal Func<HttpMessageHandler> CreateReadinessHandler { get; init; } =
        () => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };

    public override bool CanHandle(OperationKind kind) => kind is OperationKind.PipelineDeploy;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!IsLinux)
        {
            await onOutput("Deploy operations are only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        // The app name is the OperationTarget; re-validate (it is also the systemd instance + dir name).
        var app = target?.Trim() ?? string.Empty;
        if (!OperationTargetValidator.DeployAppRegex().IsMatch(app))
        {
            await onOutput($"Invalid deploy app name '{app}'", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        envVars.TryGetValue("AETHEUS_DEPLOY_ARTIFACT_ID", out var artifactIdStr);
        envVars.TryGetValue("AETHEUS_DEPLOY_RUN_ID", out var runIdStr);
        envVars.TryGetValue("AETHEUS_DEPLOY_KIND", out var deployKind);
        envVars.TryGetValue("AETHEUS_DEPLOY_COMPOSE", out var composeRel);
        // Optional dedicated health-gate window (binary mode); 0/absent ⇒ the agent derives it from timeout/3.
        envVars.TryGetValue("AETHEUS_DEPLOY_HEALTH_TIMEOUT", out var healthTimeoutStr);
        _ = int.TryParse(healthTimeoutStr, out var healthTimeoutSeconds);
        envVars.TryGetValue("AETHEUS_DEPLOY_HEALTH_URL", out var healthUrlValue);
        var (healthUrlValid, healthUrl) = await ResolveHealthUrlAsync(healthUrlValue, onOutput).ConfigureAwait(false);
        if (!healthUrlValid) return new ExecutorResult(-1, false);

        if (!int.TryParse(artifactIdStr, out var artifactId) || !int.TryParse(runIdStr, out var runId))
        {
            await onOutput("Missing AETHEUS_DEPLOY_ARTIFACT_ID / AETHEUS_DEPLOY_RUN_ID", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var isContainer = string.Equals(deployKind, "container", StringComparison.OrdinalIgnoreCase);
        if (!IsValidComposePath(isContainer, composeRel))
        {
            await onOutput($"Invalid or unsafe compose path '{composeRel}'", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        // Serialize concurrent deploys of the SAME app on this host (flip→restart critical section)
        // via an exclusive lock file - argv-free equivalent of flock on $BASE/<app>/.lock.
        var appDir = Path.Combine(DeployRoot, app);
        Directory.CreateDirectory(appDir);
        FileStream? appLock = null;
        try
        {
            appLock = await AcquireAppLockAsync(appDir, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (appLock is null) return new ExecutorResult(-1, false);

            await onOutput($"Downloading artifact {artifactId} for deploy run {runId}…", TaskLogLevel.Info).ConfigureAwait(false);
            await using var zip = await apiClient.DownloadArtifactAsync(artifactId, runId, ct).ConfigureAwait(false);
            if (zip is null)
            {
                await onOutput("Artifact download failed (not found / forbidden)", TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(1, false);
            }

            // Extract into a FRESH release dir - never the live one. On a same-runId re-run the existing
            // releases/<runId> may still be the `current` target; deleting it would kill the running app.
            var releasesRoot = Path.Combine(appDir, "releases");
            var releaseDir = MakeFreshReleaseDir(releasesRoot, runId);
            await onOutput($"Unpacking into {releaseDir}…", TaskLogLevel.Info).ConfigureAwait(false);
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Read))
            {
                var expandedBytes = DeployLayout.ValidateArchive(archive, releaseDir);
                // Use the archive's declared uncompressed size, not a compression-ratio guess: a
                // high-ratio zip bomb must be rejected before it can fill the prod filesystem.
                if (!EnsureFreeSpace(appDir, expandedBytes, out var freeMsg))
                {
                    await onOutput(freeMsg, TaskLogLevel.Error).ConfigureAwait(false);
                    return new ExecutorResult(1, false);
                }
                DeployLayout.ExtractSafely(archive, releaseDir);
            }
            DeployReleaseFileSystem.HardenTree(releaseDir);

            // ADR-021 phase 2: backend-computed app env (OTEL vars) carried under AETHEUS_DEPLOY_APPENV_*.
            var appEnv = ExtractAppEnv(envVars);

            return isContainer
                ? await DeployContainerAsync(app, releaseDir, composeRel!, appEnv, timeoutSeconds, onOutput, ct).ConfigureAwait(false)
                : await DeployBinaryAsync(app, appDir, releasesRoot, releaseDir, runId, appEnv, timeoutSeconds, healthTimeoutSeconds, healthUrl, onOutput, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(ex, "Deploy of app {App} failed", app);
            await onOutput($"Deploy failed: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        finally
        {
            if (appLock is not null) await appLock.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(bool IsValid, string? Url)> ResolveHealthUrlAsync(
        string? value,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (string.IsNullOrWhiteSpace(value)) return (true, null);
        if (DeployHealthUrlValidator.TryNormalize(value, out var normalized)) return (true, normalized);
        await onOutput(
            "Invalid AETHEUS_DEPLOY_HEALTH_URL: only absolute loopback HTTP(S) URLs are allowed",
            TaskLogLevel.Error).ConfigureAwait(false);
        return (false, null);
    }

    private static bool IsValidComposePath(bool isContainer, string? composePath) =>
        !isContainer || DeployLayout.IsSafeRelativePath(composePath);

    // ── App env injection (ADR-021 phase 2 zero-config OTLP) ────────────────────────────────────
    private const string AppEnvPrefix = "AETHEUS_DEPLOY_APPENV_";

    internal static Dictionary<string, string> ExtractAppEnv(IReadOnlyDictionary<string, string> envVars)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in envVars)
            if (key.StartsWith(AppEnvPrefix, StringComparison.Ordinal))
                result[key[AppEnvPrefix.Length..]] = value;
        return result;
    }

    /// <summary>
    /// Writes the backend-computed app env into <c>&lt;dir&gt;/.aetheus-env</c> (KEY=VALUE lines) for the
    /// systemd <c>EnvironmentFile</c> (binary) / compose <c>--env-file</c> (container). Returns the path,
    /// or null when there is nothing to inject. File mode 0640: agent-owned and readable only by
    /// the dedicated deploy group used by the app's transient systemd identity.
    /// </summary>
    internal static string? WriteAppEnvFile(string dir, IReadOnlyDictionary<string, string> appEnv)
    {
        if (appEnv.Count == 0) return null;
        var path = Path.Combine(dir, ".aetheus-env");
        var sb = new StringBuilder();
        foreach (var (key, value) in appEnv)
            sb.Append(key).Append('=').Append(value.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
        File.WriteAllText(path, sb.ToString());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        return path;
    }

    // ── Binary mode ────────────────────────────────────────────────────────────────────────────
    private async Task<ExecutorResult> DeployBinaryAsync(
        string app, string appDir, string releasesRoot, string releaseDir, int runId,
        IReadOnlyDictionary<string, string> appEnv, int timeoutSeconds,
        int healthTimeoutSeconds, string? healthUrl,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        // The `run` launcher is frequently nested (the collector keeps the publish/<app>/ prefix), so
        // resolve the directory that actually holds it and point `current` there - not blindly at the root.
        var appRoot = DeployLayout.ResolveAppRoot(releaseDir);
        if (appRoot is null)
        {
            await onOutput("Artifact has no (unique) 'run' launcher - binary deploy cannot start the app", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        DeployReleaseFileSystem.TryMakeExecutable(Path.Combine(appRoot, "run"), logger);

        // Write the backend-computed app env into the release so the systemd unit's
        // `EnvironmentFile=-<app>/current/.aetheus-env` loads it (current → appRoot after the flip).
        WriteAppEnvFile(appRoot, appEnv);

        var currentLink = Path.Combine(appDir, "current");
        var previousTarget = DeployReleaseFileSystem.ReadLinkTarget(currentLink); // null on first deploy

        if (!await ActivateReleaseAsync(
                app, appDir, currentLink, appRoot, previousTarget, timeoutSeconds, healthTimeoutSeconds, healthUrl, onOutput, ct)
            .ConfigureAwait(false))
            return new ExecutorResult(1, false);

        await PruneOldReleasesAsync(releasesRoot, currentLink, previousTarget, onOutput).ConfigureAwait(false);
        await onOutput($"Deploy succeeded - {UnitTemplatePrefix}{app} is active", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    /// <summary>
    /// Points <c>current</c> at the new release, restarts it and runs the health gate. From the flip
    /// on, every exit that is not a healthy release (failed gate, cancellation, unexpected error)
    /// rolls back to <paramref name="previousTarget"/> on a budget of its own, because the task token
    /// may already be cancelled and a rollback cut short leaves an unvalidated release running.
    /// </summary>
    internal async Task<bool> ActivateReleaseAsync(
        string app, string appDir, string currentLink, string appRoot, string? previousTarget,
        int timeoutSeconds, int healthTimeoutSeconds, string? healthUrl,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        bool healthy;
        try
        {
            // AGC-008: the flip is inside the compensated section. `mv -T` may have renamed `current`
            // when a cancellation kills it and throws; that exit must roll back too. A flip that reports
            // its own failure renamed nothing (the rename is atomic), so it returns without one.
            if (!await FlipCurrentAsync(appDir, currentLink, appRoot, onOutput, ct).ConfigureAwait(false))
                return false;
            await onOutput($"current → {appRoot}", TaskLogLevel.Info).ConfigureAwait(false);
            healthy = await RestartAndHealthCheckAsync(app, timeoutSeconds, healthTimeoutSeconds, healthUrl, onOutput, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            if (StillPointsAt(currentLink, previousTarget)) throw;
            await onOutput("Deploy interrupted after current was switched; rolling back.", TaskLogLevel.Error).ConfigureAwait(false);
            await RollbackBinaryAsync(app, appDir, currentLink, previousTarget, timeoutSeconds, healthTimeoutSeconds, healthUrl, onOutput)
                .ConfigureAwait(false);
            throw;
        }
        if (healthy) return true;
        await RollbackBinaryAsync(app, appDir, currentLink, previousTarget, timeoutSeconds, healthTimeoutSeconds, healthUrl, onOutput)
            .ConfigureAwait(false);
        return false;
    }

    // An exit before the rename took effect leaves the previous release in place: nothing to roll back,
    // and restarting it would be an outage of its own. Anything else (renamed, unreadable) is compensated.
    private static bool StillPointsAt(string currentLink, string? previousTarget)
    {
        if (previousTarget is null) return false;
        try
        {
            return string.Equals(
                DeployReleaseFileSystem.ReadLinkTarget(currentLink), previousTarget, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static CancellationTokenSource CreateCompensationBudget(int timeoutSeconds, int healthTimeoutSeconds) =>
        new(TimeSpan.FromSeconds(timeoutSeconds + DeployLayout.ResolveHealthWindowSeconds(timeoutSeconds, healthTimeoutSeconds) + 120));

    private async Task<bool> FlipCurrentAsync(
        string appDir, string currentLink, string targetDir,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        // Atomic flip: ln -sfn <targetDir> current.tmp ; mv -T current.tmp current.
        var tmpLink = Path.Combine(appDir, "current.tmp");
        var ln = await shell.RunExecAsync("ln", ["-sfn", targetDir, tmpLink], ct, AgentRuntimeDefaults.ShellCommandTimeout).ConfigureAwait(false);
        if (ln.ExitCode != 0)
        {
            await onOutput($"Symlink stage failed: {ln.StdErr}", TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
        var mv = await shell.RunExecAsync("mv", ["-T", tmpLink, currentLink], ct, AgentRuntimeDefaults.ShellCommandTimeout).ConfigureAwait(false);
        if (mv.ExitCode != 0)
        {
            await onOutput($"Symlink flip failed: {mv.StdErr}", TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private async Task RollbackBinaryAsync(
        string app, string appDir, string currentLink, string? previousTarget, int timeoutSeconds,
        int healthTimeoutSeconds, string? healthUrl,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        using var budget = CreateCompensationBudget(timeoutSeconds, healthTimeoutSeconds);
        var ct = budget.Token;
        if (previousTarget is null)
        {
            await onOutput("Health gate failed - no previous release to roll back to; app is DOWN", TaskLogLevel.Error).ConfigureAwait(false);
            return;
        }

        await onOutput($"Health gate failed - rolling back to {Path.GetFileName(previousTarget)}", TaskLogLevel.Error).ConfigureAwait(false);
        if (!await FlipCurrentAsync(appDir, currentLink, previousTarget, onOutput, ct).ConfigureAwait(false))
        {
            await onOutput("Rollback FAILED to re-point current - app is DOWN", TaskLogLevel.Error).ConfigureAwait(false);
            return;
        }
        // Capture the rollback's OWN outcome - a silent rollback that also fails leaves prod down.
        if (await RestartAndHealthCheckAsync(app, timeoutSeconds, healthTimeoutSeconds, healthUrl, onOutput, ct).ConfigureAwait(false))
        {
            await onOutput("Rollback restored the previous release - app is active again", TaskLogLevel.Warning).ConfigureAwait(false);
            // RLBK: structured signal - the deploy failed but prod was restored. The run view reads
            // PipelineStepRunDto.OutputVariables["DEPLOY_ROLLED_BACK"] to show a distinct "Rolled back"
            // badge instead of a bare "Failed", so the operator sees the app is up, not down.
            await onOutput("##aetheus[setvariable name=DEPLOY_ROLLED_BACK]true", TaskLogLevel.Info).ConfigureAwait(false);
        }
        else
            await onOutput("Rollback ALSO failed the health gate - app is DOWN, manual intervention required", TaskLogLevel.Error).ConfigureAwait(false);
    }

    private async Task<bool> RestartAndHealthCheckAsync(
        string app, int timeoutSeconds, int healthTimeoutSeconds, string? healthUrl,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        await onOutput($"Restarting {UnitTemplatePrefix}{app} via deploy-restart helper…", TaskLogLevel.Info).ConfigureAwait(false);
        var restart = await shell.RunExecAsync("sudo", ["-n", RestartHelperPath, app], ct, TimeSpan.FromSeconds(Math.Min(60, timeoutSeconds))).ConfigureAwait(false);
        if (restart.ExitCode != 0)
        {
            await onOutput($"Restart helper failed: {restart.StdErr}", TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }

        // Crash-loop-aware health gate (Restart=always would otherwise let a flapping binary read
        // is-active=0 between restarts and pass): require the unit to reach AND HOLD active(running)
        // with NO new auto-restarts across a stabilization window. An explicit health_timeout (step
        // field) wins when set - decoupling a slow .NET cold start (JIT/migrations) from the global
        // timeout; otherwise the window is derived from the step timeout (timeout/3).
        var unit = $"{UnitTemplatePrefix}{app}.service";
        var windowSeconds = DeployLayout.ResolveHealthWindowSeconds(timeoutSeconds, healthTimeoutSeconds);
        var attempts = windowSeconds / PollSeconds;
        int? baselineRestarts = null;
        var stableHits = 0;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var state = await ReadUnitStateAsync(unit, ct).ConfigureAwait(false);
            // Per-sample decision extracted to DeployLayout.EvaluateHealthSample (unit-tested).
            // CrashLooped: NRestarts climbed above baseline. Pass: a CONTINUOUS running dwell of
            // RequiredStableSamples×PollSeconds (> the unit's RestartSec) - so a warmup-then-crash
            // loop can't false-pass on two early running reads; it trips the NRestarts check instead.
            var decision = DeployLayout.EvaluateHealthSample(state, ref baselineRestarts, ref stableHits, RequiredStableSamples);
            if (decision == DeployLayout.HealthDecision.CrashLooped)
            {
                await onOutput($"Health gate failed - {unit} crash-looped ({state.NRestarts - baselineRestarts!.Value} auto-restart(s) during stabilization)", TaskLogLevel.Error).ConfigureAwait(false);
                return false;
            }
            if (decision == DeployLayout.HealthDecision.Pass)
            {
                if (healthUrl is null) return true;
                if (await ProbeFunctionalReadinessAsync(healthUrl, ct).ConfigureAwait(false))
                {
                    await onOutput($"Functional readiness passed: {healthUrl}", TaskLogLevel.Info).ConfigureAwait(false);
                    return true;
                }
            }
            await Task.Delay(HealthPollInterval, ct).ConfigureAwait(false);
        }
        var requirement = healthUrl is null
            ? "a stable active(running) state"
            : $"a stable active(running) state with HTTP 2xx readiness from {healthUrl}";
        await onOutput($"Health gate failed - {unit} did not hold {requirement} in ~{windowSeconds}s", TaskLogLevel.Error).ConfigureAwait(false);
        return false;
    }

    [SuppressMessage("Aetheus.Security", "SEC006",
        Justification = "Readiness probe of a just-restarted unit: at most one client per PollSeconds sample inside the health window, disposed with its no-redirect, no-proxy handler. Moving it to IHttpClientFactory means injecting the factory into this executor and registering a named client; not done here.")]
    private async Task<bool> ProbeFunctionalReadinessAsync(string healthUrl, CancellationToken ct)
    {
        using var handler = CreateReadinessHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(healthUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<DeployLayout.UnitState> ReadUnitStateAsync(string unit, CancellationToken ct)
    {
        var show = await shell.RunExecAsync(
            "systemctl", ["show", unit, "--property=ActiveState", "--property=SubState", "--property=NRestarts"],
            ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return DeployLayout.ParseUnitState(show.StdOut);
    }

    // ── Container mode ─────────────────────────────────────────────────────────────────────────
    internal async Task<ExecutorResult> DeployContainerAsync(
        string app, string releaseDir, string composeRel, IReadOnlyDictionary<string, string> appEnv,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        // The deployment module does NOT grant the docker group (that's the separate --enable-docker
        // opt-in). Probe the daemon up-front and fail HONESTLY rather than at a confusing mid-deploy
        // 'permission denied' - green must mean the stack really came up.
        if (!await DockerProbe.IsAvailableAsync(shell, ct).ConfigureAwait(false))
        {
            await onOutput("Docker daemon not reachable for the agent user - container deploy needs the docker group (install the agent with --enable-docker). Skipping honestly, not faking success.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var composePath = Path.Combine(releaseDir, composeRel);
        if (!File.Exists(composePath))
        {
            await onOutput($"Compose file not found in artifact: {composeRel}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var project = $"aetheus-app-{app}";

        // Snapshot the currently-tagged image ids BEFORE docker load overwrites those tags, so a failed
        // `up --wait` can be rolled back by re-tagging the previous images (image-store only, no disk tar).
        var previousImages = await CaptureComposeImageIdsAsync(project, composePath, ct).ConfigureAwait(false);

        // `docker save` lands as a tar (optionally gzipped) inside the zip - load every one we find.
        var tars = Directory.EnumerateFiles(releaseDir, "*.tar*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tars.Count == 0)
        {
            await onOutput("No docker image tarball (*.tar/*.tar.gz) found in artifact", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        // AGC-008: from the first `docker load` on, the compose tags may name images no health gate has
        // accepted, and the next recreate of the stack would start them. Every exit from here that is
        // not a healthy `up` (a failed or cancelled load, the env file, `up` itself) restores them.
        var compensate = false;
        ShellExecResult up;
        try
        {
            foreach (var tar in tars)
            {
                await onOutput($"docker load -i {Path.GetFileName(tar)}…", TaskLogLevel.Info).ConfigureAwait(false);
                compensate = true;
                var load = await shell.RunExecAsync("docker", ["load", "-i", tar], ct, TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
                if (load.ExitCode != 0)
                {
                    await onOutput($"docker load failed: {load.StdErr}", TaskLogLevel.Error).ConfigureAwait(false);
                    compensate = false;
                    await RollbackContainerAsync(project, composePath, previousImages, timeoutSeconds, onOutput).ConfigureAwait(false);
                    return new ExecutorResult(1, false);
                }
            }

            // ADR-021 phase 2: pass the backend-computed OTEL env to the stack via --env-file (compose
            // substitutes ${VAR} and exposes them to services referencing env_file/environment).
            var envFile = WriteAppEnvFile(releaseDir, appEnv);
            var upArgs = new List<string> { "compose", "-p", project, "-f", composePath };
            if (envFile is not null)
                upArgs.AddRange(["--env-file", envFile]);
            upArgs.AddRange(["up", "-d", "--wait"]);

            // --wait makes compose block until containers are running/healthy; its exit code is the health gate.
            await onOutput($"docker compose up -d --wait ({composeRel})…", TaskLogLevel.Info).ConfigureAwait(false);
            up = await shell.RunExecAsync("docker", [.. upArgs], ct,
                TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
            compensate = false;
        }
        catch when (compensate)
        {
            await onOutput("Deploy interrupted after the new images were loaded; rolling back.", TaskLogLevel.Error).ConfigureAwait(false);
            await RollbackContainerAsync(project, composePath, previousImages, timeoutSeconds, onOutput).ConfigureAwait(false);
            throw;
        }
        if (up.ExitCode != 0)
        {
            await onOutput($"compose up failed: {up.StdErr}", TaskLogLevel.Error).ConfigureAwait(false);
            await RollbackContainerAsync(project, composePath, previousImages, timeoutSeconds, onOutput).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        await onOutput($"Deploy succeeded - compose stack for {app} is up", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    // Map each compose image reference (tag) → the image id it currently resolves to, BEFORE load.
    private async Task<Dictionary<string, string>> CaptureComposeImageIdsAsync(string project, string composePath, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = await shell.RunExecAsync("docker", ["compose", "-p", project, "-f", composePath, "config", "--images"], ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (images.ExitCode != 0) return map;
        foreach (var tag in images.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var inspect = await shell.RunExecAsync("docker", ["image", "inspect", "--format", "{{.Id}}", tag], ct, AgentRuntimeDefaults.ShellCommandTimeout).ConfigureAwait(false);
            var id = inspect.StdOut.Trim();
            if (inspect.ExitCode == 0 && !string.IsNullOrEmpty(id)) map[tag] = id;
        }
        return map;
    }

    /// <summary>
    /// Best-effort container rollback (accepted limitation - see decision log). Re-tags the previously
    /// running image id (captured before <c>docker load</c>) back onto the compose tag, then re-ups the
    /// stack <c>--wait</c>. It is NOT a guaranteed rollback: it cannot restore an image that was already
    /// overwritten with no captured id (first deploy), one garbage-collected between deploy and rollback,
    /// or a multi-service/multi-tar topology only partially covered by <paramref name="previousImages"/>.
    /// A guaranteed rollback would require snapshotting the image before deploy (disk cost, in tension
    /// with the free-space gate). The trade-off is intentional: every failure here is honestly surfaced
    /// (no previous image ⇒ explicit "stack is DOWN"; failed re-up ⇒ "manual intervention required"),
    /// so it NEVER reports a false-green - it just may not be able to recover automatically.
    /// </summary>
    private async Task RollbackContainerAsync(
        string project, string composePath, Dictionary<string, string> previousImages, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        using var budget = CreateCompensationBudget(timeoutSeconds, 0);
        var ct = budget.Token;
        if (previousImages.Count == 0)
        {
            await onOutput("Container rollback: no previous image to restore (first deploy) - stack is DOWN", TaskLogLevel.Error).ConfigureAwait(false);
            return;
        }
        await onOutput("Restoring previous container image(s) and re-upping the stack…", TaskLogLevel.Warning).ConfigureAwait(false);
        foreach (var (tag, id) in previousImages)
        {
            var retag = await shell.RunExecAsync("docker", ["tag", id, tag], ct, AgentRuntimeDefaults.ShellCommandTimeout).ConfigureAwait(false);
            if (retag.ExitCode == 0) continue;
            // The tag still names the new image: re-upping would restart the release that just failed
            // and announce it as the previous one.
            await onOutput(
                $"Container rollback could not restore the previous image of {tag} ({retag.StdErr.Trim()}) - stack is DOWN, manual intervention required",
                TaskLogLevel.Error).ConfigureAwait(false);
            return;
        }
        // --wait so the rollback's success is gated on the restored containers ACTUALLY coming up healthy
        // (symmetric with the binary path's re-health-check) - not just a clean `up` exit while they crash.
        var up = await shell.RunExecAsync("docker", ["compose", "-p", project, "-f", composePath, "up", "-d", "--wait"], ct, TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
        if (up.ExitCode == 0)
        {
            await onOutput("Container rollback restored the previous stack (health-gated)", TaskLogLevel.Warning).ConfigureAwait(false);
            // RLBK: same structured signal as the binary path - failed deploy, but the stack is back up.
            await onOutput("##aetheus[setvariable name=DEPLOY_ROLLED_BACK]true", TaskLogLevel.Info).ConfigureAwait(false);
        }
        else
            await onOutput($"Container rollback ALSO failed the health gate - stack is DOWN, manual intervention required: {up.StdErr}", TaskLogLevel.Error).ConfigureAwait(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────
    private async Task<FileStream?> AcquireAppLockAsync(string appDir, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var lockPath = Path.Combine(appDir, ".lock");
        var attempts = Math.Clamp(timeoutSeconds, 30, 300); // derive from the step timeout (1s/attempt)
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(LockRetryDelay, ct).ConfigureAwait(false);
            }
        }
        await onOutput("Another deploy of this app is in progress (lock timeout)", TaskLogLevel.Error).ConfigureAwait(false);
        return null;
    }

    // A unique release dir under releases/ - never reuses a live one (same-runId re-run safety).
    private static string MakeFreshReleaseDir(string releasesRoot, int runId)
    {
        Directory.CreateDirectory(releasesRoot);
        var baseName = Path.Combine(releasesRoot, runId.ToString());
        if (!Directory.Exists(baseName))
        {
            Directory.CreateDirectory(baseName);
            return baseName;
        }
        // releases/<runId> already exists (and may be the live `current` target) - pick a fresh sibling.
        var unique = $"{baseName}__{Guid.NewGuid():N}"[..(baseName.Length + 2 + 8)];
        Directory.CreateDirectory(unique);
        return unique;
    }

    private bool EnsureFreeSpace(string appDir, long expandedBytes, out string message)
    {
        message = string.Empty;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(appDir));
            if (string.IsNullOrEmpty(root)) return true; // can't resolve a drive - don't block
            var available = new DriveInfo(root).AvailableFreeSpace;
            // DEPV: the configurable floor remains; payload bytes come from validated ZIP metadata.
            var minFreeBytes = (long)_options.DeployMinFreeSpaceMiB * 1024 * 1024;
            if (DeployLayout.HasEnoughFreeSpaceForExpandedPayload(available, expandedBytes, minFreeBytes)) return true;
            message = $"Insufficient free space to deploy: {available / (1024 * 1024)} Mo free, need ~{(minFreeBytes + expandedBytes) / (1024 * 1024)} Mo - refusing to risk filling the disk.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Free-space probe failed; proceeding without the gate");
            return true; // a probe failure must not block a legitimate deploy
        }
    }

    // Prune old releases after a green deploy: keep the N newest, never the current/previous targets.
    private async Task PruneOldReleasesAsync(string releasesRoot, string currentLink, string? previousTarget, Func<string, TaskLogLevel, Task> onOutput)
    {
        try
        {
            if (!Directory.Exists(releasesRoot)) return;
            var dirs = Directory.EnumerateDirectories(releasesRoot)
                .Select(d => (Dir: d, Time: Directory.GetLastWriteTimeUtc(d)))
                .OrderByDescending(t => t.Time)
                .Select(t => t.Dir)
                .ToList();

            // Safety: never prune when we can't resolve what `current` points at - deleting the live
            // release would take the app down. A null here means a transient readlink failure, so bail.
            var currentTop = DeployLayout.TopLevelReleaseDir(DeployReleaseFileSystem.ReadLinkTarget(currentLink), releasesRoot);
            if (currentTop is null) return;

            var protectedDirs = new HashSet<string>(StringComparer.Ordinal) { currentTop };
            var prevTop = DeployLayout.TopLevelReleaseDir(previousTarget, releasesRoot);
            if (prevTop is not null) protectedDirs.Add(prevTop);

            foreach (var dir in DeployLayout.SelectPrunableReleaseDirs(dirs, _options.DeployReleasesToKeep, protectedDirs))
            {
                Directory.Delete(dir, recursive: true);
                await onOutput($"Pruned old release {Path.GetFileName(dir)}", TaskLogLevel.Info).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Release pruning skipped");
        }
    }

}
