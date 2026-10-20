// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The Apache service operations and log reading run through an injected process runner and an
/// injected file probe, so every argv, the log path fallback and the platform refusal are asserted on any
/// platform without Apache, sudo or tail.</summary>
public class ApacheOperationExecutorRunnerTests
{
    private static ApacheOperationExecutor Build(
        ProcessRunCalls calls, bool isLinux = true, Func<string, bool>? logFileExists = null) =>
        new(
            Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 5, MaxTimeoutSeconds = 100 }),
            NullLogger<ApacheOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            LogFileExists = logFileExists ?? (_ => false),
            RunProcessWithLoggerAsync = calls.RunAsync
        };

    private static Task<ExecutorResult> Run(
        ApacheOperationExecutor executor, OperationKind kind, string target, OutputLog log, int timeout = 60) =>
        executor.ExecuteAsync(kind, target, timeout, log.Write, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(OperationKind.ApacheReload, new[] { "-n", "/bin/systemctl", "reload", "apache2.service" })]
    [InlineData(OperationKind.ApacheStart, new[] { "-n", "/bin/systemctl", "start", "apache2.service" })]
    [InlineData(OperationKind.ApacheStop, new[] { "-n", "/bin/systemctl", "stop", "apache2.service" })]
    [InlineData(OperationKind.ApacheRestart, new[] { "-n", "/bin/systemctl", "restart", "apache2.service" })]
    [InlineData(OperationKind.ApacheTestConfig, new[] { "-n", "/usr/local/lib/aetheus/aetheus-apache-configtest" })]
    public async Task ServiceControl_RunsTheExactSudoArgvOfTheSudoersAllowList(OperationKind kind, string[] argv)
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(4, false));

        var result = await Run(Build(calls), kind, "-", new OutputLog());

        Assert.Equal(4, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(argv, run.Argv);
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

        await Run(Build(calls), OperationKind.ApacheReload, "-", new OutputLog(), requested);

        Assert.Equal(expected, Assert.Single(calls.Runs).TimeoutSeconds);
    }

    [Theory]
    [InlineData(OperationKind.ApacheReload)]
    [InlineData(OperationKind.ApacheGetLogs)]
    public async Task OutsideLinux_RefusesWithoutRunningAnything(OperationKind kind)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls, isLinux: false), kind, "error", log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("only supported on Linux", TaskLogLevel.Error));
    }

    [Theory]
    [InlineData("error", "/var/log/apache2/error.log")]
    [InlineData("access", "/var/log/apache2/access.log")]
    [InlineData("anything-else", "/var/log/apache2/error.log")]
    public async Task GetLogs_TailsTheHundredLastLinesOfTheDebianLog(string target, string path)
    {
        var calls = new ProcessRunCalls();

        var result = await Run(Build(calls, logFileExists: p => p == path), OperationKind.ApacheGetLogs, target, new OutputLog());

        Assert.Equal(0, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("tail", run.FileName);
        Assert.Equal(["-n", "100", path], run.Argv);
    }

    [Theory]
    [InlineData("error", "/var/log/httpd/error_log")]
    [InlineData("access", "/var/log/httpd/access_log")]
    public async Task GetLogs_FallsBackToTheRedHatLogWhenTheDebianOneIsMissing(string target, string path)
    {
        var calls = new ProcessRunCalls();

        await Run(Build(calls, logFileExists: p => p == path), OperationKind.ApacheGetLogs, target, new OutputLog());

        Assert.Equal(["-n", "100", path], Assert.Single(calls.Runs).Argv);
    }

    [Fact]
    public async Task GetLogs_PrefersTheDebianLogWhenBothExist()
    {
        var calls = new ProcessRunCalls();

        await Run(Build(calls, logFileExists: _ => true), OperationKind.ApacheGetLogs, "error", new OutputLog());

        Assert.Equal("/var/log/apache2/error.log", Assert.Single(calls.Runs).Argv[2]);
    }

    [Fact]
    public async Task GetLogs_WithNoLogFile_WarnsAndSucceedsEmptyWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheGetLogs, "access", log);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("No Apache access log found", TaskLogLevel.Warning));
    }

    [Fact]
    public async Task GetLogs_ReturnsTheExitCodeOfTail()
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, false));

        var result = await Run(Build(calls, logFileExists: _ => true), OperationKind.ApacheGetLogs, "error", new OutputLog());

        Assert.Equal(1, result.ExitCode);
    }
}
