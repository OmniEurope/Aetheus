// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// ADR-024 4.1: applies pending OS updates via the controlled-sudo recipe. Two modes ride in
/// <see cref="PatchingConstants.DryRunEnvVar"/>. In BOTH modes the executor first runs the non-mutating
/// <c>apt-get -s upgrade</c> simulation and reports exactly what would change; it then ABORTS honestly
/// (exit 1, real log) if any <see cref="CriticalPackages"/> - or a per-server override - would be
/// upgraded. Only when nothing is blocked (and not a dry-run) does it apply via
/// <c>sudo -n /usr/bin/apt-get upgrade -y</c> against the argv-exact <c>aetheus-patch</c> drop-in.
/// Never a fake success: a blocked or failed run is red with the real output.
/// </summary>
public sealed class SystemPackageUpgradeExecutor(
    IOptions<AetheusAgentOptions> options,
    IShellRunner shell,
    ILogger<SystemPackageUpgradeExecutor> logger) : EnvironmentOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    public override bool CanHandle(OperationKind kind) => kind == OperationKind.SystemPackageUpgrade;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await onOutput("System package upgrade is only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        return await UpgradeAsync(envVars, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The upgrade itself, past the platform check: simulate, report, refuse a blocked package,
    /// stop on a dry run or when nothing is pending, and only then apply.</summary>
    internal async Task<ExecutorResult> UpgradeAsync(
        IReadOnlyDictionary<string, string> envVars, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        var dryRun = envVars.TryGetValue(PatchingConstants.DryRunEnvVar, out var d) && d == "1";
        var extraBlocklist = envVars.TryGetValue(PatchingConstants.BlocklistEnvVar, out var b) && !string.IsNullOrWhiteSpace(b)
            ? b.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        await AptLock.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 1) Non-mutating simulation (no sudo). Capture stdout so we can parse + gate on it.
            var sim = await shell.RunExecAsync("apt-get", ["-s", "upgrade"], cancellationToken).ConfigureAwait(false);
            if (sim.ExitCode != 0)
            {
                await onOutput("apt-get simulation failed; aborting (no changes made)", TaskLogLevel.Error).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(sim.StdErr))
                    await onOutput(sim.StdErr.Trim(), TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(sim.ExitCode, false);
            }

            var pending = AptSimulateParser.Parse(sim.StdOut);
            await ReportPendingAsync(pending, onOutput).ConfigureAwait(false);

            // 2) Critical-package abort (honest, before any change).
            var blocked = AptSimulateParser.BlockedPackages(pending, extraBlocklist);
            if (blocked.Count > 0)
            {
                await onOutput(
                    $"Aborting: the upgrade would touch blocked critical package(s): {string.Join(", ", blocked)}. "
                    + "Upgrade these manually in a maintenance window.", TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(1, false);
            }

            if (dryRun)
            {
                await onOutput("Dry-run complete; no changes applied.", TaskLogLevel.Info).ConfigureAwait(false);
                return new ExecutorResult(0, false);
            }

            if (pending.Count == 0)
            {
                await onOutput("Nothing to upgrade; system is up to date.", TaskLogLevel.Info).ConfigureAwait(false);
                return new ExecutorResult(0, false);
            }

            // 3) Apply via controlled-sudo (argv-exact against the aetheus-patch drop-in).
            return await AptProcessRunner.RunSudoAsync(
                BuildAptUpgradeArgv(), timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AptLock.Gate.Release();
        }
    }

    private static async Task ReportPendingAsync(
        IReadOnlyList<PendingUpdateDto> pending, Func<string, TaskLogLevel, Task> onOutput)
    {
        var securityCount = pending.Count(p => p.IsSecurity);
        await onOutput($"{pending.Count} package(s) would be upgraded ({securityCount} security).", TaskLogLevel.Info).ConfigureAwait(false);
        foreach (var p in pending)
            await onOutput($"  {p.Package} {p.CurrentVersion} -> {p.CandidateVersion}{(p.IsSecurity ? " [security]" : "")}", TaskLogLevel.Info).ConfigureAwait(false);
    }

    // `sudo -n /usr/bin/apt-get upgrade -y` - argv-exact, matches the aetheus-patch allow-list one-to-one.
    internal static IReadOnlyList<string> BuildAptUpgradeArgv() =>
        ["-n", "/usr/bin/apt-get", "upgrade", "-y"];
}
