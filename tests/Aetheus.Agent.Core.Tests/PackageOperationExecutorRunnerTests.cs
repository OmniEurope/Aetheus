// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The install/uninstall steps of <see cref="PackageOperationExecutor"/> run through an injected
/// runner, so the argv of every step, the order, the clamped timeout and the failure handling are asserted
/// on any platform without apt or sudo.</summary>
public class PackageOperationExecutorRunnerTests
{
    private static PackageOperationExecutor Build(ProcessRunCalls calls, bool isLinux = true) =>
        new(
            Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 5, MaxTimeoutSeconds = 100 }),
            NullLogger<PackageOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            RunSudoAsync = calls.RunSudoAsync
        };

    [Fact]
    public async Task Install_RefreshesTheIndexThenInstallsWithTheClampedTimeout()
    {
        var calls = new ProcessRunCalls();

        var result = await Build(calls).ExecuteAsync(
            OperationKind.ServiceInstall, "nginx", 1000, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, calls.Runs.Count);
        Assert.Equal(["-n", "/usr/bin/apt-get", "update"], calls.Runs[0].Argv);
        Assert.Equal(["-n", "/usr/bin/apt-get", "install", "-y", "nginx"], calls.Runs[1].Argv);
        Assert.All(calls.Runs, r => Assert.Equal(100, r.TimeoutSeconds));
    }

    [Fact]
    public async Task Install_WithATimeoutBelowTheMinimum_RunsWithTheMinimum()
    {
        var calls = new ProcessRunCalls();

        await Build(calls).ExecuteAsync(
            OperationKind.ServiceInstall, "nginx", 1, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.All(calls.Runs, r => Assert.Equal(5, r.TimeoutSeconds));
    }

    [Fact]
    public async Task Install_WhenTheIndexRefreshFails_WarnsAndStillInstalls_ReturningTheInstallResult()
    {
        var calls = new ProcessRunCalls(index => index == 0 ? new ExecutorResult(100, false) : new ExecutorResult(7, false));
        var log = new OutputLog();

        var result = await Build(calls).ExecuteAsync(
            OperationKind.ServiceInstall, "nginx", 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(2, calls.Runs.Count);
        Assert.True(log.Has("apt-get update failed; continuing with the cached package index", TaskLogLevel.Warning));
    }

    [Fact]
    public async Task Install_WhenTheIndexRefreshSucceeds_DoesNotWarn()
    {
        var log = new OutputLog();

        await Build(new ProcessRunCalls()).ExecuteAsync(
            OperationKind.ServiceInstall, "nginx", 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task Uninstall_PurgesTheNamedPackage_WithoutRefreshingTheIndex()
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(3, false));

        var result = await Build(calls).ExecuteAsync(
            OperationKind.ServiceUninstall, "nginx", 60, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(["-n", "/usr/bin/apt-get", "purge", "-y", "nginx"], run.Argv);
    }

    [Fact]
    public async Task OutsideLinux_RefusesWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Build(calls, isLinux: false).ExecuteAsync(
            OperationKind.ServiceInstall, "nginx", 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("only supported on Linux", TaskLogLevel.Error));
    }

    [Fact]
    public async Task ARunnerThatThrows_ReleasesTheAptLock_SoTheNextOperationRuns()
    {
        var healthy = Build(new ProcessRunCalls());
        var throwing = new PackageOperationExecutor(
            Options.Create(new AetheusAgentOptions()), NullLogger<PackageOperationExecutor>.Instance)
        {
            IsLinux = true,
            RunSudoAsync = (_, _, _, _, _) => throw new InvalidOperationException("sudo is gone")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => throwing.ExecuteAsync(
            OperationKind.ServiceUninstall, "nginx", 60, new OutputLog().Write, TestContext.Current.CancellationToken));

        var next = await healthy.ExecuteAsync(
            OperationKind.ServiceUninstall, "nginx", 60, new OutputLog().Write, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, next.ExitCode);
    }
}
