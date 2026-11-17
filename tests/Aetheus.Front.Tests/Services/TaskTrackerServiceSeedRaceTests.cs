// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Services;

/// <summary>
/// The read of <c>/api/tasks/active</c> after a (re)connect is a snapshot of the moment the server answered it.
/// A task pushed over the hub while that read is still in flight is newer than the snapshot and must survive it:
/// the E2E SignalR-drop probe lost its task exactly so (the counter never appeared, nothing brought the task back).
/// </summary>
public class TaskTrackerServiceSeedRaceTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public TaskTrackerServiceSeedRaceTests() => _handler = BunitTestHelper.RegisterServices(this);

    private TaskTrackerService CreateService() => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<AuthStateProvider>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        NullLogger<TaskTrackerService>.Instance);

    private static ServerTaskDto MakeTask(int id, TaskExecutionStatus status = TaskExecutionStatus.Pending) =>
        new() { Id = id, Name = $"task-{id}", Status = status, CreatedAt = new DateTime(2026, 1, 1).AddDays(id) };

    private static void Invoke(TaskTrackerService sut, string method, params object?[] args) =>
        typeof(TaskTrackerService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(sut, args);

    private static Task Seed(TaskTrackerService sut) =>
        (Task)typeof(TaskTrackerService).GetMethod("SeedActiveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(sut, [CancellationToken.None])!;

    /// <summary>Answers <c>api/tasks/active</c> with <paramref name="snapshot"/> only once released, and says when asked.</summary>
    private (Task Reached, TaskCompletionSource Release) HoldTheActiveRead(List<ServerTaskDto> snapshot)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/tasks/active", async ct =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(ct);
            return snapshot;
        });
        return (reached.Task, release);
    }

    [Fact]
    public async Task ATaskPushedWhileTheActiveReadIsInFlight_SurvivesTheOlderSnapshot()
    {
        var (reached, release) = HoldTheActiveRead([MakeTask(100, TaskExecutionStatus.Running)]);
        var sut = CreateService();

        var seeding = Seed(sut);
        await reached.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        // TaskQueued arrives before the snapshot, which was read by the server before this task existed.
        Invoke(sut, "Upsert", MakeTask(200));
        release.SetResult();
        await seeding;

        Assert.Equivalent(new[] { 100, 200 }, sut.Tasks.Select(task => task.Id));
    }

    [Fact]
    public async Task ATaskCompletedWhileTheActiveReadIsInFlight_IsNotBroughtBackByTheSnapshot()
    {
        var (reached, release) = HoldTheActiveRead([MakeTask(100, TaskExecutionStatus.Running), MakeTask(101, TaskExecutionStatus.Running)]);
        var sut = CreateService();

        var seeding = Seed(sut);
        await reached.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        Invoke(sut, "HandleTerminal", new TaskCompletedNotification { TaskId = 100, Status = TaskExecutionStatus.Success });
        release.SetResult();
        await seeding;

        Assert.Equal([101], sut.Tasks.Select(task => task.Id));
    }

    [Fact]
    public async Task ATaskStartedWhileTheActiveReadIsInFlight_IsShownRunningWhateverTheSnapshotSaid()
    {
        var (reached, release) = HoldTheActiveRead([MakeTask(100)]);
        var sut = CreateService();

        var seeding = Seed(sut);
        await reached.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        Invoke(sut, "MarkRunning", 100, (DateTime?)new DateTime(2026, 2, 2));
        release.SetResult();
        await seeding;

        var task = Assert.Single(sut.Tasks);
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
    }

    [Fact]
    public async Task APushAfterTheActiveReadHasEnded_IsNotReplayedByTheNextRead()
    {
        var (reached, release) = HoldTheActiveRead([MakeTask(100)]);
        var sut = CreateService();
        var seeding = Seed(sut);
        await reached.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        release.SetResult();
        await seeding;

        // Removed by a push when no read ran, then a second read that still lists it: the read is authoritative.
        Invoke(sut, "HandleTerminal", new TaskCompletedNotification { TaskId = 100, Status = TaskExecutionStatus.Success });
        Assert.Empty(sut.Tasks);
        await Seed(sut);

        Assert.Equal([100], sut.Tasks.Select(task => task.Id));
    }
}
