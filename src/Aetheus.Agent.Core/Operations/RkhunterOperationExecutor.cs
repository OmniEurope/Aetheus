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

    public bool CanHandle(OperationKind kind) => kind is
        OperationKind.RkhunterScan or
        OperationKind.RkhunterUpdate or
        OperationKind.RkhunterPropupd;

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
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
