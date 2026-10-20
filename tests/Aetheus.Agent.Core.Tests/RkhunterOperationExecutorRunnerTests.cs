// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The rkhunter operations run through an injected process runner, so the sudo helper argv, the
/// clamped timeout, the warning counting and the exit-code rewrite are asserted on any platform.</summary>
public class RkhunterOperationExecutorRunnerTests
{
    private static RkhunterOperationExecutor Build(ProcessRunCalls calls, bool isLinux = true) =>
        new(
            Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 5, MaxTimeoutSeconds = 100 }),
            NullLogger<RkhunterOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            RunProcessAsync = calls.RunAsync
        };

    private static Task<ExecutorResult> Run(RkhunterOperationExecutor executor, OperationKind kind, OutputLog log, int timeout = 60) =>
        executor.ExecuteAsync(kind, "-", timeout, log.Write, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(OperationKind.RkhunterScan, "check")]
    [InlineData(OperationKind.RkhunterUpdate, "update")]
    [InlineData(OperationKind.RkhunterPropupd, "propupd")]
    public async Task EachKind_RunsTheRootOwnedHelperWithItsWord(OperationKind kind, string word)
    {
        var calls = new ProcessRunCalls();

        var result = await Run(Build(calls), kind, new OutputLog());

        Assert.Equal(0, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(["-n", RkhunterOperationExecutor.HelperPath, word], run.Argv);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(1000, 100)]
    [InlineData(30, 30)]
    public async Task TheTimeoutIsClampedToTheConfiguredBounds(int requested, int expected)
    {
        var calls = new ProcessRunCalls();

        await Run(Build(calls), OperationKind.RkhunterUpdate, new OutputLog(), requested);

        Assert.Equal(expected, Assert.Single(calls.Runs).TimeoutSeconds);
    }

    [Fact]
    public async Task OutsideLinux_RefusesWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls, isLinux: false), OperationKind.RkhunterScan, log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("only supported on Linux", TaskLogLevel.Error));
    }

    [Fact]
    public async Task AKindWithoutAHelperWord_FailsWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();

        var result = await Run(Build(calls), OperationKind.ServiceStart, new OutputLog());

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
    }

    [Fact]
    public async Task AScanThatFoundWarnings_ExitsOneButIsReportedCompleted_WithTheWarningCount()
    {
        var calls = new ProcessRunCalls(
            _ => new ExecutorResult(1, false),
            ["Warning: Suspicious file /tmp/a", "Info: ok", "Warning: Hidden file /dev/.b"]);
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.RkhunterScan, log);

        Assert.Equal(0, result.ExitCode);
        Assert.True(log.Has("reported 2 warning(s)", TaskLogLevel.Warning));
        Assert.Equal(3, log.Lines.Count(l => l.Level == TaskLogLevel.Info));
    }

    [Fact]
    public async Task AScanThatCouldNotRun_KeepsItsExitCodeOne_AndForwardsTheOutput()
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, false), ["Unable to determine if you are root"]);
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.RkhunterScan, log);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Unable to determine if you are root", Assert.Single(log.Lines).Message);
    }

    [Fact]
    public async Task AnUpdateThatPrintsWarningLines_KeepsItsExitCode()
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, false), ["Warning: mirror unreachable"]);
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.RkhunterUpdate, log);

        Assert.Equal(1, result.ExitCode);
        Assert.False(log.Has("reported", TaskLogLevel.Warning));
    }

    [Fact]
    public async Task AScanThatTimedOut_IsNeverReportedCompleted()
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, true), ["Warning: partial"]);
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.RkhunterScan, log);

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.TimedOut);
        Assert.False(log.Has("reported", TaskLogLevel.Warning));
    }
}
