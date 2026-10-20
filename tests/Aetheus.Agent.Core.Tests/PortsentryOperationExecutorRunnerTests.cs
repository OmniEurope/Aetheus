// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The Portsentry operations run through an injected process runner, so the argv of every kind, the
/// IP and setup-parameter checks, the clamped timeout and the platform refusal are asserted on any platform.</summary>
public class PortsentryOperationExecutorRunnerTests
{
    private static readonly Dictionary<string, string> NoEnv = new(StringComparer.Ordinal);

    private static PortsentryOperationExecutor Build(ProcessRunCalls calls, bool isLinux = true) =>
        new(
            Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 5, MaxTimeoutSeconds = 100 }),
            NullLogger<PortsentryOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            RunProcessAsync = calls.RunAsync
        };

    private static Task<ExecutorResult> Run(
        PortsentryOperationExecutor executor, OperationKind kind, string target, OutputLog log, int timeout = 60) =>
        executor.ExecuteAsync(kind, target, timeout, log.Write, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(OperationKind.PortsentryStart, "start")]
    [InlineData(OperationKind.PortsentryStop, "stop")]
    [InlineData(OperationKind.PortsentryRestart, "restart")]
    public async Task ServiceControl_RunsSystemctlThroughSudo(OperationKind kind, string verb)
    {
        var calls = new ProcessRunCalls();

        var result = await Run(Build(calls), kind, "-", new OutputLog());

        Assert.Equal(0, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(["-n", "/bin/systemctl", verb, "portsentry.service"], run.Argv);
    }

    [Fact]
    public async Task Status_ReadsIsActiveWithoutSudo()
    {
        var calls = new ProcessRunCalls();

        await Run(Build(calls), OperationKind.PortsentryStatus, "-", new OutputLog());

        var run = Assert.Single(calls.Runs);
        Assert.Equal("systemctl", run.FileName);
        Assert.Equal(["is-active", "portsentry.service"], run.Argv);
    }

    [Fact]
    public async Task GetLogs_ReadsTheLastHundredJournalLinesWithoutSudo()
    {
        var calls = new ProcessRunCalls();

        await Run(Build(calls), OperationKind.PortsentryGetLogs, "-", new OutputLog());

        var run = Assert.Single(calls.Runs);
        Assert.Equal("journalctl", run.FileName);
        Assert.Equal(["-u", "portsentry.service", "-n", "100", "--no-pager"], run.Argv);
    }

    [Fact]
    public async Task AKindWithoutACommand_FailsWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();

        var result = await Run(Build(calls), OperationKind.ServiceStart, "-", new OutputLog());

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(1000, 100)]
    [InlineData(30, 30)]
    public async Task TheTimeoutIsClampedToTheConfiguredBounds(int requested, int expected)
    {
        var calls = new ProcessRunCalls();

        await Run(Build(calls), OperationKind.PortsentryStart, "-", new OutputLog(), requested);

        Assert.Equal(expected, Assert.Single(calls.Runs).TimeoutSeconds);
    }

    [Theory]
    [InlineData(OperationKind.PortsentryStart)]
    [InlineData(OperationKind.PortsentryGetLogs)]
    [InlineData(OperationKind.PortsentryUnblock)]
    public async Task OutsideLinux_RefusesWithoutRunningAnything(OperationKind kind)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls, isLinux: false), kind, "203.0.113.5", log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("only supported on Linux", TaskLogLevel.Error));
    }

    [Theory]
    [InlineData("203.0.113.5")]
    [InlineData("2001:db8::1")]
    public async Task Unblock_PassesTheAddressToTheRootOwnedHelper_AndReportsTheUnblock(string ip)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.PortsentryUnblock, ip, log);

        Assert.Equal(0, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(["-n", PortsentryOperationExecutor.UnblockHelperPath, ip], run.Argv);
        Assert.True(log.Has($"Unblocked {ip}", TaskLogLevel.Info));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.5; rm -rf /")]
    [InlineData("")]
    public async Task Unblock_RefusesAnInvalidAddress_WithoutRunningAnything(string target)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.PortsentryUnblock, target, log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("Invalid IP address", TaskLogLevel.Error));
    }

    [Fact]
    public async Task Unblock_WhenTheHelperFails_ReturnsItsExitCodeAndDoesNotClaimTheUnblock()
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, false));
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.PortsentryUnblock, "203.0.113.5", log);

        Assert.Equal(1, result.ExitCode);
        Assert.False(log.Has("Unblocked", TaskLogLevel.Info));
    }

    [Fact]
    public async Task Unblock_WhenTheHelperTimesOut_DoesNotClaimTheUnblock()
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(0, true));
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.PortsentryUnblock, "203.0.113.5", log);

        Assert.True(result.TimedOut);
        Assert.False(log.Has("Unblocked", TaskLogLevel.Info));
    }

    [Fact]
    public async Task Setup_RunsTheRootOwnedHelperWithModeAndPortLists_AndAnnouncesIt()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PortsentrySetupEnv.TcpPorts] = "22,80",
            [PortsentrySetupEnv.UdpPorts] = "53"
        };

        var result = await Build(calls).ExecuteAsync(
            OperationKind.PortsentrySetup, "atcp", env, 1000, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(["-n", PortsentryOperationExecutor.SetupHelperPath, "atcp", "22,80", "53"], run.Argv);
        Assert.Equal(100, run.TimeoutSeconds);
        Assert.True(log.Has("Configuring portsentry (mode atcp)", TaskLogLevel.Info));
    }

    [Fact]
    public async Task Setup_OutsideLinux_RefusesAValidRequestWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PortsentrySetupEnv.TcpPorts] = "22",
            [PortsentrySetupEnv.UdpPorts] = "53"
        };

        var result = await Build(calls, isLinux: false).ExecuteAsync(
            OperationKind.PortsentrySetup, "atcp", env, 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("only supported on Linux", TaskLogLevel.Error));
    }

    [Fact]
    public async Task OtherKindsIgnoreTheEnvironment_AndRunTheirOwnCommand()
    {
        var calls = new ProcessRunCalls();

        await Build(calls).ExecuteAsync(
            OperationKind.PortsentryStop, "-", NoEnv, 60, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.Equal(["-n", "/bin/systemctl", "stop", "portsentry.service"], Assert.Single(calls.Runs).Argv);
    }
}
