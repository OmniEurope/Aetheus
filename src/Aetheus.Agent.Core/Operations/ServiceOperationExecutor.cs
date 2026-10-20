// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// F-32: typed service operations. On Linux every verb (start/stop/restart/status and enable) runs as
/// <c>sudo -n /bin/systemctl …</c> against the argv-exact AETHEUS_SYSTEMCTL / AETHEUS_SERVICE_ENABLE sudoers
/// allow-lists (#11) - bare <c>systemctl</c> would be a no-op for the non-root agent on system units.
/// Windows uses <c>sc</c>. Service name is validated against <see cref="OperationTargetPatterns.ServiceName"/>.
/// </summary>
public sealed class ServiceOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<ServiceOperationExecutor> logger) : IOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    internal bool IsWindows { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    internal Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, ILogger, CancellationToken, Task<ExecutorResult>>
        RunProcessAsync
    { get; init; } = static (startInfo, timeoutSeconds, onOutput, processLogger, ct) =>
            ProcessRunner.RunAsync(startInfo, timeoutSeconds, onOutput, processLogger, ct);

    public bool CanHandle(OperationKind kind) => kind is
        OperationKind.ServiceStart or
        OperationKind.ServiceStop or
        OperationKind.ServiceRestart or
        OperationKind.ServiceStatus or
        OperationKind.ServiceGetLogs or
        OperationKind.ServiceEnable;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
        // ServiceGetLogs is the only service op carrying env vars (line count + follow flag).
        => kind == OperationKind.ServiceGetLogs
            ? GetLogsAsync(target, envVars, timeoutSeconds, onOutput, cancellationToken)
            : ExecuteAsync(kind, target, timeoutSeconds, onOutput, cancellationToken);

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        var targetFailure = await OperationExecutorFailure.ValidateTargetAsync(
            kind, target, onOutput, () => logger.LogWarning("Rejected service name with invalid format"),
            "Invalid service name format").ConfigureAwait(false);
        if (targetFailure is not null) return targetFailure;

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        // Phase 3 (option B): enable-on-boot via the controlled-sudo recipe. Linux only - server
        // management is not supported on Windows. Argv is `sudo -n /bin/systemctl enable --now <unit>`,
        // matching the argv-exact /etc/sudoers.d/aetheus-service-enable allow-list one-to-one; a
        // unit outside that fixed list is refused by sudo at the OS level. `--now` preserves the
        // original `enable --now` semantics (enable + start) from the YAML-deploy path.
        if (kind == OperationKind.ServiceEnable)
        {
            if (!IsLinux)
            {
                await onOutput("Service enable is only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(-1, false);
            }

            var enablePsi = new ProcessStartInfo
            {
                FileName = "sudo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            enablePsi.ArgumentList.Add("-n");
            enablePsi.ArgumentList.Add("/bin/systemctl");
            enablePsi.ArgumentList.Add("enable");
            enablePsi.ArgumentList.Add("--now");
            enablePsi.ArgumentList.Add(NormalizeUnit(target));
            return await RunProcessAsync(enablePsi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
        }

        // F-32 hardening: ServiceRestart on Windows is implemented as sequential `sc stop` / `sc start`
        // using ProcessStartInfo.ArgumentList (no shell, no string interpolation). This eliminates the
        // previous PowerShell `Restart-Service -Command` interpolation seam.
        if (kind == OperationKind.ServiceRestart && IsWindows)
        {
            var stopPsi = BuildScCommand("stop", target);
            var stopResult = await RunProcessAsync(stopPsi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
            if (stopResult.TimedOut)
                return stopResult;

            var startPsi = BuildScCommand("start", target);
            return await RunProcessAsync(startPsi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
        }

        var psi = IsWindows
            ? BuildWindowsArgs(kind, target)
            : BuildLinuxArgs(kind, target);

        if (psi is null)
        {
            await onOutput("Service operation not supported on this platform", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var result = await RunProcessAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
        if (IsLinux && result.ExitCode != 0 && !result.TimedOut && ServiceStartDiagnostics.Applies(kind))
            await ExplainFailedStartAsync(NormalizeUnit(target), onOutput, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal Func<string, bool> HelperExists { get; init; } = File.Exists;

    // Recette R3-44: why the unit refused to start, appended to the task's log (its status, then the
    // configuration check of its helper). The task keeps the exit code of the start itself.
    private async Task ExplainFailedStartAsync(string unit, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        await onOutput($"--- {unit} did not start: status and configuration check ---", TaskLogLevel.Warning).ConfigureAwait(false);
        foreach (var argv in ServiceStartDiagnostics.Commands(unit, HelperExists))
        {
            var psi = SudoProcessStartInfo.Create();
            foreach (var arg in argv)
                psi.ArgumentList.Add(arg);
            await RunProcessAsync(psi, DiagnosticTimeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    private const int DiagnosticTimeoutSeconds = 60;

    // ServiceGetLogs: journal read via argv-only journalctl (no shell interpolation - the former path
    // built a `journalctl -u {name} …` shell string). Linux-only (journalctl is a systemd tool). No sudo:
    // journal read works for the agent user on user-readable units; system units need the agent in the
    // systemd-journal group, which install-agent-linux.sh adds to the unit on every install and upgrade
    // (read-only host observation rights, ADR-052).
    private async Task<ExecutorResult> GetLogsAsync(
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (!OperationTargetValidator.IsValid(OperationKind.ServiceGetLogs, target))
        {
            logger.LogWarning("Rejected service name with invalid format");
            await onOutput("Invalid service name format", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        if (!IsLinux)
        {
            await onOutput("Service logs (journalctl) are only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        envVars.TryGetValue(ServiceLogsEnv.Lines, out var linesRaw);
        envVars.TryGetValue(ServiceLogsEnv.Follow, out var followRaw);
        var lines = int.TryParse(linesRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, 10000) : 100;
        var follow = bool.TryParse(followRaw, out var f) && f;

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        var psi = new ProcessStartInfo
        {
            FileName = "journalctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in BuildJournalctlArgv(target, lines, follow))
            psi.ArgumentList.Add(arg);

        return await RunProcessAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
    }

    // The journalctl argv (unit-testable without spawning). NormalizeUnit keeps parity with the other
    // service ops so "apache2" and "apache2.service" both target the same unit.
    internal static IReadOnlyList<string> BuildJournalctlArgv(string target, int lines, bool follow)
    {
        var argv = new List<string>
        {
            "-u", NormalizeUnit(target),
            "-n", lines.ToString(CultureInfo.InvariantCulture),
            "--no-pager"
        };
        if (follow) argv.Add("-f");
        return argv;
    }

    private static ProcessStartInfo? BuildLinuxArgs(OperationKind kind, string target)
    {
        var argv = BuildLinuxSystemctlArgv(kind, target);
        if (argv is null) return null;

        // #11: route start/stop/restart/status through `sudo -n /bin/systemctl <verb> <unit>` rather than
        // bare `systemctl`. System units need root, so a bare call was a no-op (or polkit-dependent) for a
        // non-root agent; this matches the argv-exact AETHEUS_SYSTEMCTL sudoers allow-list one-to-one (a unit
        // outside that fixed set is refused by sudo at the OS level). Mirrors the ServiceEnable path.
        var psi = SudoProcessStartInfo.Create();
        foreach (var arg in argv)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    // The full argv passed to `sudo` (so it can be unit-tested without spawning a process): the leading
    // `-n` (never prompt - NOPASSWD is required), the absolute `/bin/systemctl` and the bare unit name -
    // both matching the AETHEUS_SYSTEMCTL allow-list. Returns null for a non-Linux service verb.
    internal static IReadOnlyList<string>? BuildLinuxSystemctlArgv(OperationKind kind, string target)
    {
        var verb = kind switch
        {
            OperationKind.ServiceStart => "start",
            OperationKind.ServiceStop => "stop",
            OperationKind.ServiceRestart => "restart",
            OperationKind.ServiceStatus => "status",
            _ => (string?)null
        };
        if (verb is null) return null;
        return ["-n", "/bin/systemctl", verb, NormalizeUnit(target)];
    }

    internal static ProcessStartInfo? BuildWindowsArgs(OperationKind kind, string target)
    {
        // F-32 hardening: ServiceRestart is implemented as two sequential sc.exe calls
        // by ExecuteAsync (no shell interpolation). Single-verb operations build a sc.exe psi here.
        var verb = kind switch
        {
            OperationKind.ServiceStart => "start",
            OperationKind.ServiceStop => "stop",
            OperationKind.ServiceStatus => "query",
            _ => null
        };

        if (verb is null) return null;

        return BuildScCommand(verb, target);
    }

    // The service-enable sudoers allow-list uses bare unit names (apache2, nginx, …) to match the
    // existing AETHEUS_SYSTEMCTL convention. A YAML config may carry either form, so strip a trailing
    // ".service" before building argv so `apache2` and `apache2.service` both hit the same rule.
    private static string NormalizeUnit(string target) =>
        target.EndsWith(".service", StringComparison.Ordinal) ? target[..^".service".Length] : target;

    private static ProcessStartInfo BuildScCommand(string verb, string target)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(target);
        return psi;
    }
}
