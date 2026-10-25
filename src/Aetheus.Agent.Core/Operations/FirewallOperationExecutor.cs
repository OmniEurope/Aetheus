// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// ADR-024 4.2: firewall (ufw) control via the root-owned <c>aetheus-firewall</c> helper. The helper is
/// the security boundary (root-owned, agent-non-writable, re-validates every argument AND re-enforces
/// anti-lockout with the real SSH port); the sudoers grant is path-only. This executor is defence in
/// depth: it re-validates port/protocol/source and refuses an anti-lockout deny BEFORE calling the helper.
/// Everything argv-only (<c>ProcessStartInfo.ArgumentList</c>) - no shell, no interpolation.
/// </summary>
public sealed class FirewallOperationExecutor(ILogger<FirewallOperationExecutor> logger) : EnvironmentOperationExecutor
{
    public const string HelperPath = AgentHelperPaths.FirewallHelper;

    public override bool CanHandle(OperationKind kind) => kind is
        OperationKind.FirewallAllow or
        OperationKind.FirewallDeny or
        OperationKind.FirewallDeleteRule or
        OperationKind.FirewallSetEnabled;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        if (!OperationTargetValidator.IsValid(kind, target))
        {
            await onOutput("Invalid firewall operation target", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await onOutput("Firewall management is only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var command = ResolveHelperCommand(kind, target, envVars);
        if (command.Refusal is { } refusal)
        {
            await onOutput(refusal, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(command.ExitCode, false);
        }
        return await RunSudoAsync(command.Argv, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>sudo</c> argv of the helper call, or why it is refused (and with which exit code).</summary>
    internal sealed record HelperCommand(IReadOnlyList<string> Argv, string? Refusal = null, int ExitCode = 0);

    /// <summary>The helper call for a validated target: enable/disable as is, a port rule only with a
    /// valid protocol and source and never one that would close an administration port.</summary>
    internal static HelperCommand ResolveHelperCommand(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars)
    {
        // target is "enable" | "disable" (validated). The helper auto-allows the admin port on enable.
        if (kind == OperationKind.FirewallSetEnabled)
            return new HelperCommand(["-n", HelperPath, target]);

        var port = int.Parse(target);
        var proto = envVars.TryGetValue(FirewallConstants.ProtoEnvVar, out var pr) ? pr : "tcp";
        var source = envVars.TryGetValue(FirewallConstants.SourceEnvVar, out var src) && !string.IsNullOrWhiteSpace(src) ? src : "any";

        if (!FirewallValidation.IsValidProtocol(proto))
            return new HelperCommand([], $"Invalid protocol '{proto}' (allowed: tcp, udp)", -1);
        if (!FirewallValidation.IsValidSource(source))
            return new HelperCommand([], $"Invalid source '{source}' (expected a CIDR, an IP, or 'any')", -1);
        if (FirewallLockoutGuard.IsLockoutRisk(kind, port, BuildAdminPorts(envVars)))
            return new HelperCommand([], $"Refused: this would close the administration port {port} and lock you out.", 1);

        var subcommand = kind switch
        {
            OperationKind.FirewallAllow => "allow",
            OperationKind.FirewallDeny => "deny",
            OperationKind.FirewallDeleteRule => "delete",
            _ => throw new InvalidOperationException($"Unhandled firewall kind {kind}")
        };
        return new HelperCommand(["-n", HelperPath, subcommand, port.ToString(), proto, source]);
    }

    private static IReadOnlySet<int> BuildAdminPorts(IReadOnlyDictionary<string, string> envVars)
    {
        var ports = new HashSet<int>(FirewallLockoutGuard.DefaultAdminPorts);
        if (envVars.TryGetValue(FirewallConstants.AdminPortEnvVar, out var ap) && int.TryParse(ap, out var apInt))
            ports.Add(apInt);
        return ports;
    }

    private async Task<ExecutorResult> RunSudoAsync(
        IReadOnlyList<string> argv, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        var psi = SudoProcessStartInfo.Create();
        foreach (var arg in argv)
            psi.ArgumentList.Add(arg);

        return await ProcessRunner.RunAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
    }
}
