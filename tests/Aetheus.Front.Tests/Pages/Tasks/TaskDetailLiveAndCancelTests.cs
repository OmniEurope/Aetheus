// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Tasks;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Tasks;

/// <summary>
/// A task page follows the live task list (a pending task shown as started when the tracker says so)
/// and stops a task only once the operator confirms, reading the task back after the stop.
/// </summary>
public sealed class TaskDetailLiveAndCancelTests : BunitContext
{
    private const int TaskId = 4100;
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public TaskDetailLiveAndCancelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse($"api/logs/task/{TaskId}", new List<TaskLogDto>());
    }

    private static ServerTaskDto Task(TaskExecutionStatus status, DateTime? startedAt = null) => new()
    {
        Id = TaskId,
        ServerId = 9,
        ServerName = "vps",
        Name = "Service Restart - nginx",
        Status = status,
        StartedAt = startedAt,
    };

    private IRenderedComponent<TaskDetail> RenderTask(TaskExecutionStatus status)
    {
        _handler.SetJsonResponse($"api/tasks/{TaskId}", Task(status));
        var cut = Render<TaskDetail>(parameters => parameters.Add(page => page.TaskId, TaskId));
        cut.WaitForAssertion(() => Assert.Contains("Service Restart - nginx", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static string StatusBadge(IRenderedComponent<TaskDetail> cut) =>
        cut.FindAll(".omni-badge")[0].TextContent.Trim();

    private int CancelPosts => _handler.Requests.Count(request =>
        request.Method == "POST" && request.Url.EndsWith($"api/tasks/{TaskId}/cancel", StringComparison.Ordinal));

    [Fact]
    public void ALiveStatusChange_IsShownWithoutReloadingThePage()
    {
        var cut = RenderTask(TaskExecutionStatus.Pending);
        var pending = StatusBadge(cut);
        var reads = _handler.Requests.Count;
        var tracker = Services.GetRequiredService<TaskTrackerService>();

        tracker.Upsert(Task(TaskExecutionStatus.Running, new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc)) with { Id = TaskId + 1 });
        Assert.Equal(pending, StatusBadge(cut));
        tracker.Upsert(Task(TaskExecutionStatus.Pending));
        Assert.Equal(pending, StatusBadge(cut));
        tracker.Upsert(Task(TaskExecutionStatus.Running, new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc)));

        cut.WaitForAssertion(() => Assert.NotEqual(pending, StatusBadge(cut)));
        Assert.Equal(reads, _handler.Requests.Count);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void StoppingATask_PostsTheCancelOnlyOnceConfirmed(bool confirmed, int expectedPosts)
    {
        _handler.SetJsonResponse(HttpMethod.Post, $"api/tasks/{TaskId}/cancel", new { });
        _dialog.ConfirmResult = confirmed;
        var cut = RenderTask(TaskExecutionStatus.Running);
        _handler.SetJsonResponse($"api/tasks/{TaskId}", Task(TaskExecutionStatus.Cancelled));

        cut.FindAll("button").Single(button => button.TextContent.Contains("CancelTask", StringComparison.Ordinal)).Click();

        Assert.StartsWith("CancelTaskConfirm", _dialog.LastConfirmMessage, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Equal(expectedPosts, CancelPosts));
        if (confirmed)
            // The stopped task is read back: no stop button is left on a cancelled task.
            cut.WaitForAssertion(() => Assert.DoesNotContain("CancelTask", cut.Markup, StringComparison.Ordinal));
        else
            Assert.Contains("CancelTask", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedStop_LeavesTheTaskAsItWas()
    {
        _handler.SetJsonResponse(HttpMethod.Post, $"api/tasks/{TaskId}/cancel", new { }, HttpStatusCode.Conflict);
        _dialog.ConfirmResult = true;
        var cut = RenderTask(TaskExecutionStatus.Running);
        var reads = _handler.Requests.Count(request => request.Method == "GET" && request.Url.EndsWith($"api/tasks/{TaskId}", StringComparison.Ordinal));

        cut.FindAll("button").Single(button => button.TextContent.Contains("CancelTask", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(1, CancelPosts));
        Assert.Equal(reads, _handler.Requests.Count(request => request.Method == "GET" && request.Url.EndsWith($"api/tasks/{TaskId}", StringComparison.Ordinal)));
        Assert.Contains("CancelTask", cut.Markup, StringComparison.Ordinal);
    }
}
