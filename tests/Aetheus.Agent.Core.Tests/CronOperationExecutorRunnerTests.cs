// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Cron save and delete run through an injected process runner, so the argv of the root-owned helper,
/// the last-line validation of every field, the clamped timeout and the platform refusal are asserted on any
/// platform without sudo.</summary>
public class CronOperationExecutorRunnerTests
{
    private static CronOperationExecutor Build(ProcessRunCalls calls, bool isLinux = true) =>
        new(
            Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 5, MaxTimeoutSeconds = 100 }),
            NullLogger<CronOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            RunProcessAsync = calls.RunAsync
        };

    private static Dictionary<string, string> Job(
        string user = "deploy", string schedule = "*/5 * * * *", string command = "/usr/bin/backup --daily") =>
        new(StringComparer.Ordinal)
        {
            ["AETHEUS_CRON_USER"] = user,
            ["AETHEUS_CRON_SCHEDULE"] = schedule,
            ["AETHEUS_CRON_COMMAND"] = command
        };

    private static Task<ExecutorResult> Save(
        CronOperationExecutor executor, string id, Dictionary<string, string> env, OutputLog log, int timeout = 60) =>
        executor.ExecuteAsync(OperationKind.CronSave, id, env, timeout, log.Write, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Save_PassesIdUserScheduleAndCommandAsSeparateArgumentsToTheRootOwnedHelper()
    {
        var calls = new ProcessRunCalls();

        var result = await Save(Build(calls), "backup-1", Job(), new OutputLog());

        Assert.Equal(0, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(
            ["-n", CronOperationExecutor.HelperPath, "save", "backup-1", "deploy", "*/5 * * * *", "/usr/bin/backup --daily"],
            run.Argv);
    }

    [Fact]
    public async Task Delete_NeedsOnlyTheId_AndIgnoresTheJobFields()
    {
        var calls = new ProcessRunCalls();

        var result = await Build(calls).ExecuteAsync(
            OperationKind.CronDelete, "backup-1", 60, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["-n", CronOperationExecutor.HelperPath, "delete", "backup-1"], Assert.Single(calls.Runs).Argv);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(1000, 100)]
    [InlineData(30, 30)]
    public async Task TheTimeoutIsClampedToTheConfiguredBounds(int requested, int expected)
    {
        var calls = new ProcessRunCalls();

        await Save(Build(calls), "backup-1", Job(), new OutputLog(), requested);

        Assert.Equal(expected, Assert.Single(calls.Runs).TimeoutSeconds);
    }

    [Fact]
    public async Task OutsideLinux_RefusesWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Save(Build(calls, isLinux: false), "backup-1", Job(), log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("only supported on Linux", TaskLogLevel.Error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad id")]
    [InlineData("../etc")]
    [InlineData("a.b")]
    public async Task AMalformedJobId_IsRefusedForSaveAndDelete(string id)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();
        var executor = Build(calls);

        var saved = await Save(executor, id, Job(), log);
        var deleted = await executor.ExecuteAsync(
            OperationKind.CronDelete, id, 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, saved.ExitCode);
        Assert.Equal(-1, deleted.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.Equal(2, log.Lines.Count(l => l.Message == "Invalid cron job id format"));
    }

    [Theory]
    [InlineData("root", "*/5 * * * *", "/usr/bin/backup")]
    [InlineData("deploy", "*/5 * * * * *", "/usr/bin/backup")]
    [InlineData("deploy", "*/5 * * * *", "backup; rm -rf /")]
    [InlineData("deploy", "*/5 * * * *", "")]
    [InlineData("bad user", "*/5 * * * *", "/usr/bin/backup")]
    public async Task ASaveWithAnInvalidUserScheduleOrCommand_IsRefusedWithoutRunningAnything(
        string user, string schedule, string command)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Save(Build(calls), "backup-1", Job(user, schedule, command), log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.True(log.Has("Invalid cron user, schedule or command", TaskLogLevel.Error));
    }

    [Fact]
    public async Task ASaveWithoutTheJobFields_IsRefusedWithoutRunningAnything()
    {
        var calls = new ProcessRunCalls();

        var result = await Save(Build(calls), "backup-1", [], new OutputLog());

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
    }
}
