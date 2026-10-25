// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Tasks;

/// <summary>
/// R1-39: a task whose agent is not claiming is parked again on every 10 s sweep. The hold used to be
/// logged each time (425 identical lines per task over 71 minutes, 93 % of an export). It is now
/// reported once when the task enters the hold, then once more when it resumes, fails or times out.
/// </summary>
public sealed class TaskTimeoutServiceHoldLoggingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private const string HoldMarker = "is on hold";

    private static (TaskTimeoutService Sut, ITaskRepository Repo, RecordingLogger<TaskTimeoutService> Log) BuildSut(
        TimeSpan? grace = null)
    {
        var repo = Substitute.For<ITaskRepository>();
        repo.GetStaleRunningTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetStaleAssignedTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetTasksFromSupersededAgentSessionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        repo.FindPipelineStepRunsByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        repo.GetServerStatusAsync(5, Arg.Any<CancellationToken>()).Returns(ServerStatus.Online);
        repo.IsServerTaskPollingActiveAsync(5, Arg.Any<CancellationToken>()).Returns(false);

        var provider = new ServiceCollection()
            .AddScoped(_ => repo)
            .AddScoped(_ => Substitute.For<IPipelineRunService>())
            .BuildServiceProvider();

        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(clients);

        var options = Options.Create(new BackgroundServicesOptions());
        if (grace is { } value) options.Value.OfflineAgentGrace = value;

        var log = new RecordingLogger<TaskTimeoutService>();
        var sut = new TaskTimeoutService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            log,
            options,
            hub,
            new FakeTimeProvider(Now));
        return (sut, repo, log);
    }

    private static ServerTask PendingPipelineTask(int id)
        => new()
        {
            Id = id,
            ServerId = 5,
            Name = $"step-{id}",
            Status = TaskExecutionStatus.Pending,
            PipelineRunId = 1352,
            PipelineStepRunId = 9,
            Operation = OperationKind.None,
            CreatedAt = Now.UtcDateTime - TimeSpan.FromMinutes(30)
        };

    private static void Stale(ITaskRepository repo, params ServerTask[] tasks)
        => repo.GetStalePendingTasksAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([.. tasks]);

    [Fact]
    public async Task SameTaskParkedOnTwentySweeps_IsReportedOnHoldExactlyOnce()
    {
        var (sut, repo, log) = BuildSut();
        var task = PendingPipelineTask(11323);

        for (var sweep = 0; sweep < 20; sweep++)
        {
            Stale(repo, task);
            await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);
        }

        Assert.Single(log.Entries, entry => entry.Message.Contains(HoldMarker, StringComparison.Ordinal));
        Assert.Equal(TaskExecutionStatus.Pending, task.Status);
    }

    // R-7: the single line carries the reason the backend sees, read once per server when tasks enter the hold,
    // so a fourth task of the same server costs no extra read and later sweeps cost none.
    [Fact]
    public async Task HoldLine_CarriesTheDiagnosis_AndTheServerIsReadOncePerHoldEntry()
    {
        var (sut, repo, log) = BuildSut();
        var facts = new PendingHoldServerFacts(
            5, "runner-a", ServerStatus.Online, null, Now.UtcDateTime - TimeSpan.FromSeconds(10),
            Now.UtcDateTime - TimeSpan.FromMinutes(40), "0123456789abcdef", "1.4.0", 2, "[\"shell.execute\"]",
            false, null, [], 3);
        repo.GetPendingHoldFactsAsync(5, Arg.Any<CancellationToken>()).Returns(facts);
        var tasks = new[] { PendingPipelineTask(11323), PendingPipelineTask(11324), PendingPipelineTask(11325) };

        for (var sweep = 0; sweep < 5; sweep++)
        {
            Stale(repo, tasks);
            await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);
        }

        var holdLines = log.Entries.Where(entry => entry.Message.Contains(HoldMarker, StringComparison.Ordinal)).ToList();
        Assert.Equal(3, holdLines.Count);
        Assert.All(holdLines, line =>
        {
            Assert.Contains("cause=PollingLeaseLapsed", line.Message, StringComparison.Ordinal);
            Assert.Contains("server='runner-a' status=Online", line.Message, StringComparison.Ordinal);
            Assert.Contains("graceLeft=23h30m", line.Message, StringComparison.Ordinal);
        });
        await repo.Received(1).GetPendingHoldFactsAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HoldLine_IsStillWritten_WhenTheServerRowCannotBeRead()
    {
        var (sut, repo, log) = BuildSut();
        Stale(repo, PendingPipelineTask(11323));

        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Single(log.Entries, entry =>
            entry.Message.Contains(HoldMarker, StringComparison.Ordinal)
            && entry.Message.Contains("cause=ServerUnknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachNewTaskEnteringTheHold_GetsItsOwnSingleLine()
    {
        var (sut, repo, log) = BuildSut();
        var first = PendingPipelineTask(11323);
        var second = PendingPipelineTask(11324);

        Stale(repo, first);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);
        Stale(repo, first, second);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);
        Stale(repo, first, second);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        var holdLines = log.Entries.Where(entry => entry.Message.Contains(HoldMarker, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, holdLines.Count);
        Assert.Contains(holdLines, line => line.Message.Contains("#11323", StringComparison.Ordinal));
        Assert.Contains(holdLines, line => line.Message.Contains("#11324", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TaskClaimedAgain_LogsAResumeLine_AndIsReportedAgainIfItReturnsToTheHold()
    {
        var (sut, repo, log) = BuildSut();
        var task = PendingPipelineTask(11323);
        Stale(repo, task);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        // The agent claims it: it is no longer a stale Pending candidate.
        task.Status = TaskExecutionStatus.Assigned;
        Stale(repo);
        repo.FindTaskAsync(11323, Arg.Any<CancellationToken>()).Returns(task);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Contains(log.Entries, entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("resumed", StringComparison.Ordinal));

        // A later, distinct hold of the same task is a new transition and is reported again.
        task.Status = TaskExecutionStatus.Pending;
        Stale(repo, task);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, log.Entries.Count(entry => entry.Message.Contains(HoldMarker, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TaskThatTimesOutAfterTheGrace_LogsAWarningInsteadOfStayingSilent()
    {
        var (sut, repo, log) = BuildSut(grace: TimeSpan.FromMinutes(40));
        var task = PendingPipelineTask(11323);
        Stale(repo, task);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        // Same task, now older than the grace: it is no longer parked and times out.
        task.CreatedAt = Now.UtcDateTime - TimeSpan.FromMinutes(45);
        Stale(repo, task);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Timeout, task.Status);
        Assert.Contains(log.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("left its hold", StringComparison.Ordinal)
            && entry.Message.Contains("Timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TaskStillPendingButSkippedBecauseItsAgentPollsAgain_IsNotAnnouncedAsResumed()
    {
        var (sut, repo, log) = BuildSut();
        var task = PendingPipelineTask(11323);
        Stale(repo, task);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        // The agent polls again: the sweep keeps the task queued and parks nothing, yet the claim has
        // not happened, so no "resumed" line may be written.
        repo.IsServerTaskPollingActiveAsync(5, Arg.Any<CancellationToken>()).Returns(true);
        repo.FindTaskAsync(11323, Arg.Any<CancellationToken>()).Returns(task);
        Stale(repo, task);
        await sut.CheckStaleTasksAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("resumed", StringComparison.Ordinal));
        Assert.Single(log.Entries, entry => entry.Message.Contains(HoldMarker, StringComparison.Ordinal));
    }
}
