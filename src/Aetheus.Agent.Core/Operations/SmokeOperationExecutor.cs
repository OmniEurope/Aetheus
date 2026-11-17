// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// type: smoke - probe an already-deployed application and report findings as evidence.
///
/// The three deployment paths each hand-rolled these probes in shell, and each drew the
/// pass/fail line differently: a readiness sample over budget rolled back a healthy demo, while the
/// nightly path silently omitted the frontend contract checks that production enforces. This
/// executor makes the probes one implementation with one contract.
///
/// Findings are published through <c>##aetheus[setvariable]</c>, and a technical fault that makes the
/// evidence meaningless (an unusable target, a browser image that cannot run) always fails the step,
/// because a run that produced no evidence must never be graded as if it had.
///
/// What a finding does to the step is the step's own choice, through <c>gate:</c>. In
/// <c>advisory</c> mode - the default, and what every existing caller gets - findings are recorded and
/// the step still exits 0. In <c>blocking</c> mode a BLOCKING finding fails the step, which is what
/// lets a deployment pipeline compensate a bad cutover instead of merely annotating it. The
/// response-time budget is never blocking in either mode: a cold cache or a busy host is enough to
/// exceed it, and that is precisely the drift that once rolled back a demo that was serving
/// correctly.
///
/// The target is an origin, never a full URL: every probe path is composed here, so a step
/// definition cannot repoint a probe at an arbitrary endpoint.
/// </summary>
public sealed class SmokeOperationExecutor(
    IShellRunner shell,
    IOptions<AetheusAgentOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<SmokeOperationExecutor> logger) : EnvironmentOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    /// <summary>
    /// Named client for every probe aimed at a deployed origin, shared with the blue-green readiness
    /// gate. Named rather than constructed here so the probes are exercisable without a listening
    /// socket: agent tests may not open one (a Windows firewall prompt would stall the run).
    /// </summary>
    public const string DeploymentProbeHttpClientName = "AetheusDeploymentProbe";

    public override bool CanHandle(OperationKind kind) => kind == OperationKind.PipelineSmoke;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (kind != OperationKind.PipelineSmoke) return new ExecutorResult(1, false);
        if (!OperationTargetValidator.IsValid(OperationKind.PipelineSmoke, target))
        {
            await onOutput($"Invalid smoke origin '{target}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        var origin = target.TrimEnd('/');
        var findings = 0;
        var blocking = 0;

        var readiness = await ProbeReadinessAsync(origin, envVars, onOutput, ct).ConfigureAwait(false);
        if (!readiness.Answered) { findings++; blocking++; }
        else if (!readiness.WithinBudget) findings++;

        if (ShouldRun(envVars, "AETHEUS_SMOKE_FRONTEND")
            && !await ProbeFrontendContractAsync(origin, onOutput, ct).ConfigureAwait(false))
        {
            findings++;
            blocking++;
        }

        var browserImage = envVars.GetValueOrDefault("AETHEUS_SMOKE_BROWSER_IMAGE", string.Empty).Trim();
        if (browserImage.Length > 0)
        {
            var (browserRan, browserPassed) = await RunBrowserSuiteAsync(
                origin, browserImage, envVars, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            // A browser image that cannot even start produces no evidence, so it is a technical
            // fault rather than a finding: grading it as a failed suite would be a lie.
            if (!browserRan)
            {
                await onOutput("Browser smoke could not run; no evidence was produced.", TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(1, false);
            }
            if (!browserPassed) { findings++; blocking++; }
        }

        return await PublishEvidenceAsync(findings, blocking, IsBlockingGate(envVars), onOutput).ConfigureAwait(false);
    }

    /// <summary>Value of <c>gate:</c> that makes a blocking finding fail the step.</summary>
    internal const string BlockingGate = "blocking";

    internal static bool IsBlockingGate(IReadOnlyDictionary<string, string> envVars) =>
        string.Equals(
            envVars.GetValueOrDefault("AETHEUS_SMOKE_GATE", string.Empty).Trim(),
            BlockingGate,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Publishes the counters a pipeline can read, then applies the step's own gate. The variables are
    /// emitted before the verdict so the evidence survives even when the step fails on it.
    /// </summary>
    private static async Task<ExecutorResult> PublishEvidenceAsync(
        int findings, int blocking, bool blockingGate, Func<string, TaskLogLevel, Task> onOutput)
    {
        await onOutput($"##aetheus[setvariable name=SMOKE_GATE_STATUS]{(findings == 0 ? 0 : 1)}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"##aetheus[setvariable name=SMOKE_FINDINGS]{findings}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"##aetheus[setvariable name=SMOKE_BLOCKING_FINDINGS]{blocking}", TaskLogLevel.Info).ConfigureAwait(false);

        if (findings == 0)
        {
            await onOutput("Smoke evidence: all probes passed.", TaskLogLevel.Info).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }
        if (blockingGate && blocking > 0)
        {
            await onOutput(
                $"Smoke evidence: {blocking} blocking finding(s) out of {findings}; the step is gated as blocking, so the "
                + "deployment is failed rather than annotated.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        await onOutput(
            $"Smoke evidence: {findings} probe group(s) reported findings ({blocking} blocking); recorded for grading.",
            TaskLogLevel.Warning).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    private static bool ShouldRun(IReadOnlyDictionary<string, string> envVars, string key) =>
        string.Equals(envVars.GetValueOrDefault(key, "false"), "true", StringComparison.OrdinalIgnoreCase);

    private HttpClient CreateClient() => httpClientFactory.CreateClient(DeploymentProbeHttpClientName);

    /// <summary>
    /// Whether readiness answered at all, and whether it did so inside the configured budget. They
    /// are reported separately because only the first can ever be blocking: a cold cache or a busy
    /// host is enough to exceed a budget, and that must not be able to undo a deployment that answers
    /// correctly.
    /// </summary>
    private readonly record struct ReadinessEvidence(bool Answered, bool WithinBudget);

    private async Task<ReadinessEvidence> ProbeReadinessAsync(
        string origin, IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var samples = ParseBounded(envVars, "AETHEUS_SMOKE_SAMPLES", 1, 1, 50);
        var budgetSeconds = ParseBudget(envVars.GetValueOrDefault("AETHEUS_SMOKE_MAX_SECONDS", string.Empty));
        using var client = CreateClient();
        var slowest = 0d;

        for (var attempt = 1; attempt <= samples; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await client
                    .GetAsync($"{origin}/health/ready", HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                if (!response.IsSuccessStatusCode)
                {
                    await onOutput(
                        $"Readiness probe returned {(int)response.StatusCode}.", TaskLogLevel.Warning).ConfigureAwait(false);
                    return new ReadinessEvidence(false, false);
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Readiness probe failed for {Origin}", origin);
                await onOutput($"Readiness probe failed: {ex.Message}", TaskLogLevel.Warning).ConfigureAwait(false);
                return new ReadinessEvidence(false, false);
            }
            slowest = Math.Max(slowest, stopwatch.Elapsed.TotalSeconds);
        }

        await onOutput(
            $"##aetheus[setvariable name=SMOKE_MAX_SECONDS]{slowest.ToString("F3", CultureInfo.InvariantCulture)}",
            TaskLogLevel.Info).ConfigureAwait(false);
        if (budgetSeconds is { } budget && slowest > budget)
        {
            await onOutput(
                $"Readiness answered but exceeded the {budget.ToString("F3", CultureInfo.InvariantCulture)}s budget "
                + $"(slowest {slowest.ToString("F3", CultureInfo.InvariantCulture)}s). Recorded as advisory: the "
                + "application is serving.",
                TaskLogLevel.Warning).ConfigureAwait(false);
            return new ReadinessEvidence(true, false);
        }
        await onOutput(
            $"Readiness passed over {samples} sample(s); slowest {slowest.ToString("F3", CultureInfo.InvariantCulture)}s.",
            TaskLogLevel.Info).ConfigureAwait(false);
        return new ReadinessEvidence(true, true);
    }

    /// <summary>Requires a real, always-revalidated frontend HTML shell (<c>no-cache</c>, never
    /// <c>no-store</c>: see <see cref="HtmlShellCachePolicy"/>) so deployment swaps cannot leave
    /// browsers requesting runtime assets from a retired colour.</summary>
    private async Task<bool> ProbeFrontendContractAsync(
        string origin, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        using var client = CreateClient();
        try
        {
            using var response = await client.GetAsync(origin + "/", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await onOutput($"Frontend returned {(int)response.StatusCode}.", TaskLogLevel.Warning).ConfigureAwait(false);
                return false;
            }
            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase))
            {
                await onOutput("Frontend did not return an HTML shell.", TaskLogLevel.Warning).ConfigureAwait(false);
                return false;
            }
            if (!HtmlShellCachePolicy.IsRevalidated(
                    response.Headers.CacheControl is { } cacheControl ? [cacheControl.ToString()] : []))
            {
                await onOutput(
                    "Frontend shell is not revalidated on every use; a cached shell strands the app on retired runtime hashes.",
                    TaskLogLevel.Warning).ConfigureAwait(false);
                return false;
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Frontend contract probe failed for {Origin}", origin);
            await onOutput($"Frontend probe failed: {ex.Message}", TaskLogLevel.Warning).ConfigureAwait(false);
            return false;
        }
        await onOutput("Frontend contract passed (HTML shell, no-cache).", TaskLogLevel.Info).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Runs the authenticated browser suite from an immutable image. Returns whether the suite ran
    /// at all, and whether it passed. The distinction matters: "did not run" is a technical fault,
    /// "ran and failed" is evidence.
    /// </summary>
    private async Task<(bool Ran, bool Passed)> RunBrowserSuiteAsync(
        string origin, string image, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!await DockerProbe.IsAvailableAsync(shell, ct).ConfigureAwait(false))
        {
            await onOutput("Docker is not reachable for the agent user; the browser suite cannot run.", TaskLogLevel.Error).ConfigureAwait(false);
            return (false, false);
        }
        var inspect = await shell.RunExecAsync(
            "docker", ["image", "inspect", "--format", "{{.Id}}", image], ct, AgentRuntimeDefaults.ShellCommandTimeout)
            .ConfigureAwait(false);
        if (inspect.ExitCode != 0)
        {
            await onOutput($"Browser smoke image is not present: {image}", TaskLogLevel.Error).ConfigureAwait(false);
            return (false, false);
        }

        var filter = ResolveBrowserFilter(envVars);
        var project = envVars.GetValueOrDefault("AETHEUS_SMOKE_PROJECT", "tests/Aetheus.E2E/Aetheus.E2E.csproj");
        // A name the agent chose, so a container that outlives its docker client (timeout or
        // cancellation kills the client, never the container) can still be found and removed.
        var containerName = ResolveContainerName(envVars);
        var args = new List<string>
        {
            // --init: Docker's own init is PID 1, so `dotnet test` of the .NET 11 SDK is never PID 1, which never
            // exits once the suite is over (R-40, run 3289). This holds for every browser image, whatever its
            // entrypoint (the Dockerfile-built ones have none).
            "run", "--rm", "--init", "--name", containerName, "--network", "host", "--ipc", "host", "--pids-limit", "512",
            "--security-opt", "no-new-privileges",
            // Names this container as Aetheus's browser smoke in the Docker inventory, where its host
            // network is shown as expected instead of reported (PLAN-010 lot 1).
            "--label", "io.aetheus.role=browser-smoke",
            "-e", "E2E_PRESERVE_DATABASE=true",
            "-e", $"E2E_FRONTEND_URL={origin}",
            // The suite logs in against the backend URL. Defaulting it to the application origin is
            // right only when one host serves both; on a deployment whose API has its own hostname
            // the login lands on a static file server, which answers 405 and fails the whole suite in
            // OneTimeSetUp. The step now says so explicitly when the two differ.
            "-e", $"E2E_BACKEND_URL={ResolveApiOrigin(envVars, origin)}",
            "-e", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true",
            "-e", "DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK=true"
        };
        // Pass credentials by NAME only. `docker run -e KEY=value` puts the value on the agent's argv,
        // where any local process can read it from /proc/<pid>/cmdline; `-e KEY` tells Docker to
        // forward the value from the launching process environment instead, so it never appears
        // there. The values are handed to that environment below.
        var containerEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (source, mapped) in new[]
        {
            ("AETHEUS_SMOKE_ADMIN_USER", "E2E_ADMIN_EMAIL"),
            ("AETHEUS_SMOKE_ADMIN_PASSWORD", "E2E_ADMIN_PASSWORD")
        })
        {
            var value = envVars.GetValueOrDefault(source, string.Empty);
            if (value.Length == 0) continue;
            containerEnvironment[mapped] = value;
            args.AddRange(["-e", mapped]);
        }
        args.AddRange([
            image,
            "dotnet", "test", project,
            "--configuration", "Release", "--no-build", "--no-restore",
            "--filter", filter,
            "--logger", "console;verbosity=minimal"
        ]);

        var suiteTimeout = ResolveSuiteTimeoutSeconds(envVars, timeoutSeconds);
        await onOutput(
            $"Running the browser suite against {origin} in container {containerName} (limit {suiteTimeout}s)…",
            TaskLogLevel.Info).ConfigureAwait(false);

        // The suite has its own bound, far below the step timeout: a hang must cost minutes, not the
        // whole step budget (R-40: `dotnet test` as PID 1 never exited after a passing suite).
        using var suiteLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        suiteLimit.CancelAfter(TimeSpan.FromSeconds(suiteTimeout));
        ShellExecResult result;
        try
        {
            // Lines are forwarded as they arrive; the bounded capture is kept for the verdict below.
            result = await shell.RunExecStreamingAsync(
                "docker", [.. args], containerEnvironment, Environment.CurrentDirectory,
                inheritEnvironment: true, MaxCapturedOutputBytes,
                (line, isStandardError) => EmitLineAsync(
                    line, isStandardError ? TaskLogLevel.Warning : TaskLogLevel.Info, onOutput),
                suiteLimit.Token, TimeSpan.FromSeconds(timeoutSeconds))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killing the docker client does not stop the container it started: remove it by name.
            var removal = await RemoveContainerAsync(containerName, onOutput).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            await onOutput(
                $"Browser smoke exceeded its {suiteTimeout}s delay and was aborted; {removal}.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return (false, false);
        }
        if (DescribeSuiteThatDidNotRun(result) is { } notRun)
        {
            await onOutput(notRun, TaskLogLevel.Error).ConfigureAwait(false);
            return (false, false);
        }
        if (result.ExitCode == 0)
        {
            await onOutput("Browser suite passed.", TaskLogLevel.Info).ConfigureAwait(false);
            return (true, true);
        }
        await onOutput("Browser suite reported failing tests.", TaskLogLevel.Warning).ConfigureAwait(false);
        return (true, false);
    }

    /// <summary>
    /// Why a <c>docker run</c> of the suite produced no test evidence, or null when it did. Docker
    /// itself answers 125 when the container cannot start, 126 when the command cannot be invoked and
    /// 127 when it does not exist: none of them ran a test. A filter that selects no test runs none
    /// either, whatever the exit code says.
    /// </summary>
    internal static string? DescribeSuiteThatDidNotRun(ShellExecResult result)
    {
        var reason = result.ExitCode switch
        {
            125 => "docker could not start the browser suite container (exit 125)",
            126 => "the browser suite command could not be invoked in the container (exit 126)",
            127 => "the browser suite command was not found in the container (exit 127)",
            _ => null
        };
        if (reason is null
            && (result.StdOut.Contains("No test matches the given testcase filter", StringComparison.Ordinal)
                || result.StdOut.Contains("Zero tests ran", StringComparison.Ordinal)))
            reason = "the browser suite filter selected no test";
        return reason is null ? null : $"Browser suite did not run: {reason}.";
    }

    /// <summary>Test category the browser suite is restricted to when a step names no filter.</summary>
    internal const string DefaultBrowserFilter = "TestCategory=ProductionSmoke";

    /// <summary>
    /// The control plane always writes <c>AETHEUS_SMOKE_FILTER</c>, empty when the step omits
    /// <c>filter:</c>, so a dictionary default would never apply. An empty filter would hand
    /// <c>dotnet test</c> no restriction at all and run the whole suite against the live origin the
    /// deployment just switched to, so treat blank as "the production-smoke category".
    /// </summary>
    internal static string ResolveBrowserFilter(IReadOnlyDictionary<string, string> envVars)
    {
        var filter = envVars.GetValueOrDefault("AETHEUS_SMOKE_FILTER", string.Empty).Trim();
        return filter.Length > 0 ? filter : DefaultBrowserFilter;
    }

    /// <summary>
    /// The origin the browser suite authenticates against. Falls back to the application origin, so a
    /// single-host deployment behaves exactly as before and only a deployment that actually splits
    /// the two has to say so.
    /// </summary>
    internal static string ResolveApiOrigin(IReadOnlyDictionary<string, string> envVars, string origin)
    {
        var apiOrigin = envVars.GetValueOrDefault("AETHEUS_SMOKE_API_ORIGIN", string.Empty).Trim().TrimEnd('/');
        return apiOrigin.Length > 0 ? apiOrigin : origin;
    }

    /// <summary>Caps captured process output so a runaway suite log cannot exhaust agent memory.</summary>
    private const int MaxCapturedOutputBytes = 1024 * 1024;

    /// <summary>Longest single log line the backend accepts; a longer one rejects its whole batch.</summary>
    internal const int MaxLogLineLength = 8000;

    /// <summary>
    /// Emits one streamed output line, truncating it past the backend's limit: a longer line would
    /// reject its whole batch and cost the surrounding log lines too. Blank lines are dropped.
    /// </summary>
    private static Task EmitLineAsync(
        string line, TaskLogLevel level, Func<string, TaskLogLevel, Task> onOutput)
    {
        var trimmed = line.TrimEnd('\r');
        return trimmed.Length == 0
            ? Task.CompletedTask
            : onOutput(
                trimmed.Length <= MaxLogLineLength ? trimmed : trimmed[..MaxLogLineLength] + " …[truncated]",
                level);
    }

    /// <summary>Step variable that overrides the browser suite's own timeout (seconds).</summary>
    internal const string SuiteTimeoutVariable = "AETHEUS_SMOKE_SUITE_TIMEOUT_SECONDS";

    /// <summary>
    /// The bound of the browser suite: the agent option (default
    /// <see cref="AgentRuntimeDefaults.SmokeSuiteTimeoutSeconds"/>), overridable per step through
    /// <see cref="SuiteTimeoutVariable"/>, and never longer than the step timeout itself.
    /// </summary>
    private int ResolveSuiteTimeoutSeconds(IReadOnlyDictionary<string, string> envVars, int stepTimeoutSeconds)
    {
        var configured = Math.Clamp(
            _options.SmokeSuiteTimeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        var requested = ParseBounded(
            envVars, SuiteTimeoutVariable, configured, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        return Math.Min(requested, stepTimeoutSeconds);
    }

    /// <summary>
    /// A docker-valid, unique name for this suite's container: the task id (unique per execution, set
    /// by the poller) after the run id, or a random suffix when the task id is unknown.
    /// </summary>
    internal static string ResolveContainerName(IReadOnlyDictionary<string, string> envVars)
    {
        var runId = SanitizeNamePart(envVars.GetValueOrDefault("AETHEUS_RUN_ID", string.Empty));
        var taskId = SanitizeNamePart(envVars.GetValueOrDefault("AETHEUS_TASK_ID", string.Empty));
        if (taskId.Length == 0) taskId = Guid.NewGuid().ToString("N")[..12];
        return runId.Length == 0 ? $"aetheus-smoke-{taskId}" : $"aetheus-smoke-{runId}-{taskId}";
    }

    /// <summary>Keeps docker's container-name charset (<c>[a-zA-Z0-9_.-]</c>) and bounds the length.</summary>
    private static string SanitizeNamePart(string raw)
    {
        var part = new string([.. raw.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-')]);
        return part.Length <= 40 ? part : part[..40];
    }

    /// <summary>Removes the smoke container by name, whatever state the cancelled run left it in.
    /// Uses its own short timeout rather than the cancelled token, and says what became of it.</summary>
    private async Task<string> RemoveContainerAsync(string containerName, Func<string, TaskLogLevel, Task> onOutput)
    {
        var removal = await shell.RunExecAsync(
            "docker", ["rm", "-f", containerName], CancellationToken.None, ContainerRemovalTimeout)
            .ConfigureAwait(false);
        if (removal.ExitCode == 0) return $"container {containerName} was removed";
        if (removal.StdErr.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            return $"container {containerName} had already gone";
        logger.LogWarning("docker rm -f {Container} exited {Exit}: {Error}", containerName, removal.ExitCode, removal.StdErr.Trim());
        await onOutput(
            $"Container {containerName} could not be removed (docker exit {removal.ExitCode}); remove it by hand.",
            TaskLogLevel.Error).ConfigureAwait(false);
        return $"container {containerName} could NOT be removed";
    }

    private static readonly TimeSpan ContainerRemovalTimeout = TimeSpan.FromSeconds(30);

    internal static int ParseBounded(
        IReadOnlyDictionary<string, string> envVars, string key, int fallback, int min, int max) =>
        int.TryParse(envVars.GetValueOrDefault(key, string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    internal static double? ParseBudget(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
}
