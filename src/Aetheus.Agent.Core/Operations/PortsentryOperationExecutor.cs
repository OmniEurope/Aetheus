// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Typed Portsentry operations - service control, status check, log reading, and IP unblocking.
/// Each kind maps to a fixed argv; the only caller-supplied input is the IP address for
/// <see cref="OperationKind.PortsentryUnblock"/>, validated against <c>IPAddress.TryParse</c>.
/// Service control goes through the controlled-sudo recipe.
/// </summary>
public sealed class PortsentryOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<PortsentryOperationExecutor> logger) : IOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // The process runner, replaceable so each operation's argv is asserted without spawning anything.
    internal Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, ILogger, CancellationToken, Task<ExecutorResult>>
        RunProcessAsync
    { get; init; } = static (startInfo, timeoutSeconds, onOutput, processLogger, ct) =>
            ProcessRunner.RunAsync(startInfo, timeoutSeconds, onOutput, processLogger, ct);

    // Root-owned helper deposited by install-agent-linux.sh (--module server-management). The
    // matching /etc/sudoers.d/aetheus-portsentry grants exactly this binary, NOPASSWD. It removes
    // the IP from iptables/ip6tables and /etc/hosts.deny - keeping /etc/hosts.deny (a 'spawn'-capable
    // root-RCE vector) out of any standing agent ACL.
    internal const string UnblockHelperPath = "/usr/local/lib/aetheus/unblock-ip";

    // Root-owned helper deposited by install-agent-linux.sh (portsentry-manage capability). The matching
    // /etc/sudoers.d/aetheus-portsentry grants exactly this binary, NOPASSWD. It apt-installs
    // portsentry and writes the scan mode / port lists into the config, then enables the service.
    internal const string SetupHelperPath = "/usr/local/lib/aetheus/portsentry-setup";

    public bool CanHandle(OperationKind kind) => kind is
        OperationKind.PortsentryStart or
        OperationKind.PortsentryStop or
        OperationKind.PortsentryRestart or
        OperationKind.PortsentryStatus or
        OperationKind.PortsentryGetLogs or
        OperationKind.PortsentryUnblock or
        OperationKind.PortsentrySetup;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        // Setup is the only Portsentry op that carries env vars (the port lists); the rest ignore them.
        return kind == OperationKind.PortsentrySetup
            ? SetupAsync(target, envVars, timeoutSeconds, onOutput, cancellationToken)
            : ExecuteAsync(kind, target, timeoutSeconds, onOutput, cancellationToken);
    }

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var platformFailure = await OperationPlatformGuard.RequireLinuxAsync(
            onOutput, "Portsentry operations are only supported on Linux", IsLinux).ConfigureAwait(false);
        if (platformFailure is not null) return platformFailure;

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        if (kind == OperationKind.PortsentryGetLogs)
            return await GetLogsAsync(timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);

        if (kind == OperationKind.PortsentryUnblock)
            return await UnblockAsync(target, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);

        return await OperationProcessRunner.RunOptionalAsync(
            BuildPsi(kind), RunProcessAsync, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a Portsentry operation kind to a fully-resolved argv. Service control operations go
    /// through sudo; status check uses <c>systemctl is-active</c> (no sudo needed).
    /// </summary>
    internal static ProcessStartInfo? BuildPsi(OperationKind kind)
    {
        switch (kind)
        {
            case OperationKind.PortsentryStart:
            case OperationKind.PortsentryStop:
            case OperationKind.PortsentryRestart:
                {
                    var psi = SudoProcessStartInfo.Create();
                    psi.ArgumentList.Add("-n");
                    psi.ArgumentList.Add("/bin/systemctl");
                    psi.ArgumentList.Add(kind switch
                    {
                        OperationKind.PortsentryStart => "start",
                        OperationKind.PortsentryStop => "stop",
                        OperationKind.PortsentryRestart => "restart",
                        _ => "restart"
                    });
                    psi.ArgumentList.Add("portsentry.service");
                    return psi;
                }
            case OperationKind.PortsentryStatus:
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "systemctl",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add("is-active");
                    psi.ArgumentList.Add("portsentry.service");
                    return psi;
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// Reads portsentry logs via <c>journalctl</c>. No sudo needed.
    /// </summary>
    private async Task<ExecutorResult> GetLogsAsync(
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "journalctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("portsentry.service");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("100");
        psi.ArgumentList.Add("--no-pager");

        return await RunProcessAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unblocks an IP address through the root-owned <c>aetheus-unblock-ip</c> helper (argv-exact
    /// sudoers grant). The helper removes the IP from iptables/ip6tables and <c>/etc/hosts.deny</c>.
    /// The IP is validated both by <c>OperationTargetValidator</c> (shared) and locally via
    /// <c>IPAddress.TryParse</c> as a last-line defence, then passed argv-only (no shell, no
    /// interpolation). This replaces the previous in-executor <c>sudo iptables</c>/<c>sudo sed</c>
    /// steps - those needed an iptables/sed sudoers grant that was never installed (so the path was
    /// dead) and put a <c>spawn</c>-capable root-RCE file (<c>/etc/hosts.deny</c>) behind a raw sed.
    /// </summary>
    private async Task<ExecutorResult> UnblockAsync(
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        // Last-line defence: validate IP format in the executor itself
        if (!IPAddress.TryParse(target, out _))
        {
            await onOutput($"Invalid IP address: {target}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var psi = SudoProcessStartInfo.Create();
        psi.ArgumentList.Add("-n"); // never prompt - NOPASSWD is required by the sudoers rule
        psi.ArgumentList.Add(UnblockHelperPath);
        psi.ArgumentList.Add(target);

        var result = await RunProcessAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0 && !result.TimedOut)
            await onOutput($"Unblocked {target}", TaskLogLevel.Info).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Provisions/configures portsentry through the root-owned <c>portsentry-setup</c> helper (argv-exact
    /// sudoers grant). The scan mode is the target; the TCP/UDP port lists arrive in
    /// <c>AETHEUS_PORTSENTRY_*_PORTS</c> env vars. Every field is re-validated here (last-line defence)
    /// against the shared shapes and passed argv-only - no shell, no interpolation, replacing the dead
    /// <c>sed ... 2&gt;/dev/null || true &amp;&amp; apt-get install</c> chain. The helper re-validates too and is
    /// the security boundary; if it is not installed, sudo fails and the step fails honestly.
    /// </summary>
    private async Task<ExecutorResult> SetupAsync(
        string mode,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        envVars.TryGetValue(PortsentrySetupEnv.TcpPorts, out var tcpPorts);
        envVars.TryGetValue(PortsentrySetupEnv.UdpPorts, out var udpPorts);

        // Validate before the OS check so a malformed request is rejected without spawning anything.
        if (!PortsentryValidation.IsValidMode(mode) ||
            !PortsentryValidation.IsValidPortList(tcpPorts) ||
            !PortsentryValidation.IsValidPortList(udpPorts))
        {
            logger.LogWarning("Rejected portsentry setup with invalid parameters");
            await onOutput("Invalid portsentry setup parameters", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var platformFailure = await OperationPlatformGuard.RequireLinuxAsync(
            onOutput, "Portsentry operations are only supported on Linux", IsLinux).ConfigureAwait(false);
        if (platformFailure is not null) return platformFailure;

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        var psi = SudoProcessStartInfo.Create();
        // argv-exact: helper path, then mode / tcp / udp positional params. The helper re-validates each.
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(SetupHelperPath);
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(tcpPorts!);
        psi.ArgumentList.Add(udpPorts!);

        await onOutput($"Configuring portsentry (mode {mode})…", TaskLogLevel.Info).ConfigureAwait(false);
        return await RunProcessAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
    }
}
