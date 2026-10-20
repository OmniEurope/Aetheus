// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public class SystemPackageUpgradeExecutorTests
{
    private static SystemPackageUpgradeExecutor Build()
        => new(Options.Create(new AetheusAgentOptions()), Substitute.For<IShellRunner>(),
               NullLogger<SystemPackageUpgradeExecutor>.Instance);

    [Fact]
    public void CanHandle_OnlySystemPackageUpgrade()
    {
        var exec = Build();
        Assert.True(exec.CanHandle(OperationKind.SystemPackageUpgrade));
        Assert.False(exec.CanHandle(OperationKind.ServiceInstall));
    }

    [Fact]
    public void BuildAptUpgradeArgv_IsArgvExact()
        => Assert.Equal(["-n", "/usr/bin/apt-get", "upgrade", "-y"], SystemPackageUpgradeExecutor.BuildAptUpgradeArgv());

    private const string Simulation =
        "Inst curl [7.81.0-1ubuntu1.15] (7.81.0-1ubuntu1.16 Ubuntu:22.04/jammy-security [amd64])\n"
        + "Inst htop [3.0.5-7] (3.0.5-8 Ubuntu:22.04/jammy-updates [amd64])\n";

    private static (SystemPackageUpgradeExecutor Executor, IShellRunner Shell) BuildWith(ShellExecResult simulation)
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("apt-get", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-s", "upgrade" })),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(simulation);
        return (new(Options.Create(new AetheusAgentOptions()), shell, NullLogger<SystemPackageUpgradeExecutor>.Instance), shell);
    }

    private static async Task<(ExecutorResult Result, List<string> Log)> UpgradeAsync(
        SystemPackageUpgradeExecutor executor, Dictionary<string, string> env)
    {
        var log = new List<string>();
        var result = await executor.UpgradeAsync(env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        return (result, log);
    }

    [Fact]
    public async Task FailedSimulation_AbortsWithItsExitCodeAndError()
    {
        var (executor, _) = BuildWith(new ShellExecResult(100, "", "E: Could not get lock\n"));

        var (result, log) = await UpgradeAsync(executor, []);

        Assert.Equal(100, result.ExitCode);
        Assert.Equal(["apt-get simulation failed; aborting (no changes made)", "E: Could not get lock"], log);
    }

    [Fact]
    public async Task DryRun_ReportsThePendingPackagesAndChangesNothing()
    {
        var (executor, shell) = BuildWith(new ShellExecResult(0, Simulation, ""));

        var (result, log) = await UpgradeAsync(executor, new() { [PatchingConstants.DryRunEnvVar] = "1" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
        [
            "2 package(s) would be upgraded (1 security).",
            "  curl 7.81.0-1ubuntu1.15 -> 7.81.0-1ubuntu1.16 [security]",
            "  htop 3.0.5-7 -> 3.0.5-8",
            "Dry-run complete; no changes applied.",
        ], log);
        await shell.Received(1).RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task APackageOfTheServerBlocklist_AbortsBeforeAnyChange()
    {
        var (executor, _) = BuildWith(new ShellExecResult(0, Simulation, ""));

        var (result, log) = await UpgradeAsync(executor, new() { [PatchingConstants.BlocklistEnvVar] = " htop , nginx" });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(log, line => line.StartsWith("Aborting: the upgrade would touch blocked critical package(s): htop.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingPending_SucceedsWithoutApplying()
    {
        var (executor, _) = BuildWith(new ShellExecResult(0, "Reading package lists...\n", ""));

        var (result, log) = await UpgradeAsync(executor, []);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["0 package(s) would be upgraded (0 security).", "Nothing to upgrade; system is up to date."], log);
    }
}
