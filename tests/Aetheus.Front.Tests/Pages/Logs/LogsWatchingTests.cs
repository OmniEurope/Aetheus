// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using LogsPage = Aetheus.Front.Components.Logs.Logs;

namespace Aetheus.Front.Tests.Pages.Logs;

/// <summary>
/// Watching a task's live logs over a real hub connection to an in-memory logs hub: the page joins the
/// task's group, shows its stored lines, appends the lines pushed one by one or in a batch, moves to
/// another task by leaving the first group, does nothing more for the task it already watches, and joins
/// the group again after a reconnect.
/// </summary>
public sealed class LogsWatchingTests : BunitContext
{
    private static readonly DateTime T0 = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);
    private readonly BunitTestHelper.TestHandler _handler;

    public LogsWatchingTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        this.UseInMemoryHubs();
        _handler.SetJsonResponse(HttpMethod.Get, "api/logs/task/7", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 7, Message = "stored line", Timestamp = T0 }
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/logs/task/8", new List<TaskLogDto>
        {
            new() { Id = 10, TaskId = 8, Message = "other task line", Timestamp = T0 }
        });
    }

    private InMemoryHubServer LogsHub => this.Hub("logs");

    private async Task<IRenderedComponent<LogsPage>> WatchAsync(int taskId)
    {
        var cut = Render<LogsPage>();
        await WatchAsync(cut, taskId);
        return cut;
    }

    private static Task WatchAsync(IRenderedComponent<LogsPage> cut, int taskId) =>
        cut.InvokeAsync(() =>
        {
            cut.Instance._taskIdFilter = taskId;
            return cut.Instance.StartWatching();
        });

    [Fact]
    public async Task WatchingATask_JoinsItsGroup_ShowsItsStoredLines_AndAppendsPushedOnes()
    {
        var cut = await WatchAsync(7);

        Assert.Equal([7], LogsHub.IntArguments("JoinTaskGroup"));
        await cut.WhenRenderedAsync(() => Assert.Contains("stored line", cut.Markup, StringComparison.Ordinal));

        await LogsHub.PushAsync("LogReceived", new TaskLogDto { Id = 2, TaskId = 7, Message = "single line", Timestamp = T0.AddSeconds(1) });
        await LogsHub.PushAsync("LogsReceived", new List<TaskLogDto>
        {
            new() { Id = 3, TaskId = 7, Message = "batch one", Timestamp = T0.AddSeconds(2) },
            new() { Id = 4, TaskId = 7, Message = "batch two", Timestamp = T0.AddSeconds(3) }
        });

        await cut.WhenRenderedAsync(() =>
        {
            Assert.Contains("single line", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("batch two", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AnotherTask_LeavesTheFirstGroup_AndTheSameTaskChangesNothing()
    {
        var cut = await WatchAsync(7);

        await WatchAsync(cut, 8);
        await WatchAsync(cut, 8);

        Assert.Equal([7], LogsHub.IntArguments("LeaveTaskGroup"));
        Assert.Equal([7, 8], LogsHub.IntArguments("JoinTaskGroup"));
        Assert.Equal(1, LogsHub.Connections);
        await cut.WhenRenderedAsync(() =>
        {
            Assert.Contains("other task line", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("stored line", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AfterAReconnect_TheWatchedTasksGroupIsJoinedAgain()
    {
        await WatchAsync(7);

        await LogsHub.DropAsync();

        await LogsHub.WhenInvokedAsync("JoinTaskGroup", 2);
        Assert.Equal([7, 7], LogsHub.IntArguments("JoinTaskGroup"));
    }
}
