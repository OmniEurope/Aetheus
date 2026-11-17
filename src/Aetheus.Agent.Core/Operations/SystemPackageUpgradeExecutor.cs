// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// ADR-024 4.1: applies pending OS updates via the controlled-sudo recipe. Two modes ride in
/// <see cref="PatchingConstants.DryRunEnvVar"/>. In BOTH modes the executor first runs the non-mutating
/// <c>apt-get -s upgrade --with-new-pkgs</c> simulation and reports exactly what would change; it then ABORTS honestly
/// (exit 1, real log) if any <see cref="CriticalPackages"/> - or a per-server override - would be
/// upgraded. Only when nothing is blocked (and not a dry-run) does it apply via
/// <c>sudo -n /usr/bin/apt-get upgrade --with-new-pkgs -y</c> against the argv-exact <c>aetheus-patch</c> drop-in.
/// Never a fake success: a blocked or failed run is red with the real output.
/// </summary>
public sealed class SystemPackageUpgradeExecutor(
    IOptions<AetheusAgentOptions> options,
    IShellRunner shell,
    ILogger<SystemPackageUpgradeExecutor> logger,
    Func<string, bool>? fileExists = null) : EnvironmentOperationExecutor
{
    /// <summary>Where Debian and Ubuntu record that an installed package (a kernel, libc, ...) needs a restart.</summary>
    internal const string RebootRequiredPath = "/var/run/reboot-required";

    private readonly AetheusAgentOptions _options = options.Value;
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

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
            var sim = await shell.RunExecAsync("apt-get", BuildAptSimulateArgv(), cancellationToken).ConfigureAwait(false);
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
            var applied = await AptProcessRunner.RunSudoAsync(
                BuildAptUpgradeArgv(), timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
            if (applied.ExitCode == 0)
                await ReportRebootRequiredAsync(onOutput).ConfigureAwait(false);
            return applied;
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

    /// <summary>After a successful upgrade, says in the task log whether the system must restart (a new kernel is installed
    /// but only runs after a reboot). Read from the file apt's hooks write, never guessed.</summary>
    internal async Task ReportRebootRequiredAsync(Func<string, TaskLogLevel, Task> onOutput)
    {
        if (_fileExists(RebootRequiredPath))
            await onOutput($"Restart needed: the upgrade installed packages that only take effect after a reboot ({RebootRequiredPath} exists).", TaskLogLevel.Warning).ConfigureAwait(false);
    }

    // `apt-get -s upgrade --with-new-pkgs` - the unprivileged simulation. `--with-new-pkgs` lets apt install NEW packages
    // an upgrade depends on (a kernel ABI bump is a new linux-image package; plain `upgrade` keeps linux-generic back) and
    // never removes a package.
    internal static IReadOnlyList<string> BuildAptSimulateArgv() => SecurityUpdatesCollector.BuildAptSimulateArgv();

    // `sudo -n /usr/bin/apt-get upgrade --with-new-pkgs -y` - argv-exact, matches the aetheus-patch allow-list one-to-one.
    internal static IReadOnlyList<string> BuildAptUpgradeArgv() =>
        ["-n", "/usr/bin/apt-get", "upgrade", "--with-new-pkgs", "-y"];
}
