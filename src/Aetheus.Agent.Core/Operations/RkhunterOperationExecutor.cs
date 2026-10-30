// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Items #10.2/#10.3 of the plan - typed RKHunter operations under the same controlled-sudo
/// recipe Apache uses (item #6), through a root-owned helper (ADR-019). Each kind maps 1:1 to a line in
/// <c>/etc/sudoers.d/aetheus-rkhunter</c> dropped by
/// <c>install-agent-linux.sh --enable-rkhunter-manage</c>. Argv is exact - no flags from the
/// caller, no shell, no metacharacters.
/// </summary>
public sealed class RkhunterOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<RkhunterOperationExecutor> logger) : IOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // The process runner, replaceable so the argv and the warning counting are asserted without sudo.
    internal Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, ILogger, CancellationToken, Task<ExecutorResult>>
        RunProcessAsync
    { get; init; } = static (startInfo, timeoutSeconds, onOutput, processLogger, ct) =>
            ProcessRunner.RunAsync(startInfo, timeoutSeconds, onOutput, processLogger, ct);

    // R-119: one step of the install (an apt-get call or a helper word), through sudo, with an optional stdin;
    // replaceable so the sequence and its argv are asserted without sudo.
    internal Func<IReadOnlyList<string>, string?, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>>?
        RunSetupStepAsync
    { get; init; }

    public bool CanHandle(OperationKind kind) => kind is
        OperationKind.RkhunterScan or
        OperationKind.RkhunterUpdate or
        OperationKind.RkhunterPropupd or
        OperationKind.RkhunterSetup;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
        => kind == OperationKind.RkhunterSetup
            ? SetupAsync(target, envVars, timeoutSeconds, onOutput, cancellationToken)
            : ExecuteAsync(kind, target, timeoutSeconds, onOutput, cancellationToken);

    /// <summary>R-119: installs rkhunter through the argv-exact aetheus-package grant, updates its data and builds the
    /// file baseline through the helper, then writes the warning address (helper word <c>mail-on-warning</c>, the
    /// address on stdin). A failed data update (often an unreachable mirror) is reported and the baseline is still
    /// built; any other failure stops the install and fails the task.</summary>
    private async Task<ExecutorResult> SetupAsync(
        string target, IReadOnlyDictionary<string, string> envVars, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!IsLinux)
            return await FailAsync(onOutput, "RKHunter operations are only supported on Linux").ConfigureAwait(false);
        if (!OperationTargetValidator.IsValid(OperationKind.RkhunterSetup, target))
            return await FailAsync(onOutput, $"Invalid RKHunter setup target '{target}'.").ConfigureAwait(false);
        var address = envVars.GetValueOrDefault(RkhunterSetupEnv.MailOnWarning);
        if (address is not null && !RkhunterSetupEnv.IsValidMailOnWarning(address))
            return await FailAsync(onOutput, "The warning mail address is not a valid email address.").ConfigureAwait(false);

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        var run = RunSetupStepAsync ?? RunSetupStepWithSudoAsync;

        await AptLock.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A fresh host may ship an empty package index: refresh it, best effort, as the package install does.
            var update = await run(PackageOperationExecutor.BuildAptUpdateArgv(), null, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (update.ExitCode != 0)
                await onOutput("apt-get update failed; continuing with the cached package index", TaskLogLevel.Warning).ConfigureAwait(false);
            var install = await run(PackageOperationExecutor.BuildAptArgv(PackageOperationExecutor.InstallVerb, "rkhunter"),
                null, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (install.ExitCode != 0)
                return install;
        }
        finally
        {
            AptLock.Gate.Release();
        }

        var data = await run(BuildHelperArgv("update"), null, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (data.ExitCode != 0)
            await onOutput($"rkhunter --update ended with exit code {data.ExitCode}; the file baseline is built anyway, use Update database later.",
                TaskLogLevel.Warning).ConfigureAwait(false);

        var baseline = await run(BuildHelperArgv("propupd"), null, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (baseline.ExitCode != 0)
            return baseline;

        if (address is not null)
        {
            var mail = await run(BuildHelperArgv("mail-on-warning"), address + "\n", timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (mail.ExitCode != 0)
                return mail;
        }

        await onOutput("RKHunter is installed and its file baseline is built.", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    private Task<ExecutorResult> RunSetupStepWithSudoAsync(
        IReadOnlyList<string> argv, string? standardInput, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var psi = SudoProcessStartInfo.Create();
        // Kept by the aetheus-package grant (env_keep), dropped by env_reset for the helper.
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        foreach (var argument in argv) psi.ArgumentList.Add(argument);
        return ProcessRunner.RunAsync(psi, timeoutSeconds, onOutput, logger, ct, standardInput);
    }

    private static async Task<ExecutorResult> FailAsync(Func<string, TaskLogLevel, Task> onOutput, string message)
    {
        await onOutput(message, TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(-1, false);
    }

    /// <summary>The exact <c>sudo</c> argv of one helper word, as the aetheus-rkhunter grant names it.</summary>
    internal static string[] BuildHelperArgv(string word) => ["-n", HelperPath, word];

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (kind == OperationKind.RkhunterSetup)
            return await SetupAsync(target, new Dictionary<string, string>(), timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);
        if (!IsLinux)
        {
            await onOutput("RKHunter operations are only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        var psi = SudoProcessStartInfo.Create();
        var argv = BuildSudoArgv(kind);
        if (argv is null) return new ExecutorResult(-1, false);
        foreach (var arg in argv) psi.ArgumentList.Add(arg);

        var warnings = 0;
        Task Observe(string line, TaskLogLevel level)
        {
            if (IsScanWarning(line)) warnings++;
            return onOutput(line, level);
        }

        var result = await RunProcessAsync(psi, timeoutSeconds, Observe, logger, cancellationToken).ConfigureAwait(false);
        if (!IsCompletedScanWithWarnings(kind, result, warnings))
            return result;

        await onOutput(
            $"rkhunter completed the scan and reported {warnings} warning(s): its exit code 1 means warnings were found, not that the scan failed",
            TaskLogLevel.Warning).ConfigureAwait(false);
        return result with { ExitCode = 0 };
    }

    /// <summary>rkhunter exits 1 both when a completed scan found warnings and when it could not run
    /// (recette R3-36: "Unable to determine if you are root", exit 1, no warning line). Only the first
    /// prints its findings as <c>Warning:</c> lines on stdout, so the scan counts as done only then.</summary>
    internal static bool IsCompletedScanWithWarnings(OperationKind kind, ExecutorResult result, int warningLines) =>
        kind == OperationKind.RkhunterScan && result is { ExitCode: 1, TimedOut: false, FailureCode: null } && warningLines > 0;

    internal static bool IsScanWarning(string line) => line.StartsWith("Warning:", StringComparison.Ordinal);

    /// <summary>Root-owned helper rendered by <c>install-agent-linux.sh --enable-rkhunter-manage</c>
    /// from <c>deploy/agent-host-config/rkhunter/aetheus-rkhunter</c>. rkhunter is a shell script that
    /// execs grep, tr, cut and id, so it cannot run under sudo <c>noexec</c> (recette R3-36: every
    /// scan died on "Permission denied"); the helper is the ADR-019 exception that lets it.</summary>
    internal const string HelperPath = "/usr/local/lib/aetheus/aetheus-rkhunter";

    // Exact `sudo -n <helper> <word>` argv per kind (unit-testable without spawning). No
    // caller-supplied flags - each maps 1:1 to a line in /etc/sudoers.d/aetheus-rkhunter, and the
    // helper maps the word to the fixed rkhunter argv (check: --check --skip-keypress --nocolors
    // --report-warnings-only, which keeps stdout to actionable findings).
    internal static string[]? BuildSudoArgv(OperationKind kind) => kind switch
    {
        OperationKind.RkhunterScan => ["-n", HelperPath, "check"],
        OperationKind.RkhunterUpdate => ["-n", HelperPath, "update"],
        OperationKind.RkhunterPropupd => ["-n", HelperPath, "propupd"],
        _ => null
    };
}
