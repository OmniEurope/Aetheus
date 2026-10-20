// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R3-37: the follower binds to the task its page launched, from the live task feed, and says
/// what became of it: waiting, running, done, or failed with the cause read from the task's log.
/// </summary>
public sealed class ServerTaskFollowerTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerTaskFollowerTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private IRenderedComponent<ServerTaskFollower> RenderFollower() =>
        Render<ServerTaskFollower>(p => p.Add(x => x.ServerId, 5).Add(x => x.NamePrefix, "RKHunter"));

    [Fact]
    public async Task R3_44_AFailedTask_ShowsTheEndOfItsLog_NotItsLastLineAlone()
    {
        var lines = new List<TaskLogDto> { new() { Id = 1, TaskId = 50009, Message = "early line, dropped" } };
        lines.AddRange(Enumerable.Range(2, ServerTaskFollower.CauseLines - 2).Select(i => new TaskLogDto { Id = i, TaskId = 50009, Message = $"filler {i}" }));
        lines.Add(new() { Id = 100, TaskId = 50009, Message = "Job for apache2.service failed because the control process exited with error code." });
        lines.Add(new() { Id = 101, TaskId = 50009, Message = "AH00526: Syntax error on line 7 of /etc/apache2/sites-enabled/site.conf" });
        _handler.SetJsonResponse(HttpMethod.Get, "api/logs/task/50009", lines);
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Follow("Start apache2", 50009));

        await cut.InvokeAsync(() => Tracker.HandleTerminal(new TaskCompletedNotification
        {
            TaskId = 50009,
            ServerId = 5,
            TaskName = "Service - Start apache2",
            Status = TaskExecutionStatus.Failed
        }));

        var cause = cut.WaitForElement(".task-output-error").TextContent;
        Assert.Contains("Job for apache2.service failed", cause, StringComparison.Ordinal);
        Assert.Contains("AH00526: Syntax error on line 7", cause, StringComparison.Ordinal);
        Assert.DoesNotContain("early line, dropped", cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALaunchedTask_IsFollowedFromQueuedToFailed_WithTheCauseFromItsLog()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/logs/task/50001", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 50001, Message = "/usr/bin/grep: Permission denied" },
            new() { Id = 2, TaskId = 50001, Message = "Unable to determine if you are root" }
        });
        var cut = RenderFollower();

        await cut.InvokeAsync(() => cut.Instance.Expect("RunScan"));
        Assert.Contains("ServerTaskRequested", cut.Markup);

        await cut.InvokeAsync(() => Tracker.Upsert(new ServerTaskDto
        {
            Id = 50001,
            ServerId = 5,
            Name = "RKHunter - RunScan",
            Status = TaskExecutionStatus.Pending
        }));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskQueued", cut.Markup));
        Assert.Contains("/tasks/50001", cut.Markup);

        await cut.InvokeAsync(() => Tracker.HandleTerminal(new TaskCompletedNotification
        {
            TaskId = 50001,
            ServerId = 5,
            TaskName = "RKHunter - RunScan",
            Status = TaskExecutionStatus.Failed
        }));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskFailed", cut.Markup));
        Assert.Contains("Unable to determine if you are root", cut.Markup);
    }

    [Fact]
    public async Task ATaskOfAnotherServerOrAnotherKind_IsNotTheOneFollowed()
    {
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Expect("RunScan"));

        await cut.InvokeAsync(() => Tracker.Upsert(new ServerTaskDto
        {
            Id = 50002,
            ServerId = 6,
            Name = "RKHunter - RunScan",
            Status = TaskExecutionStatus.Pending
        }));
        await cut.InvokeAsync(() => Tracker.Upsert(new ServerTaskDto
        {
            Id = 50003,
            ServerId = 5,
            Name = "Apache logs - error",
            Status = TaskExecutionStatus.Pending
        }));

        Assert.Contains("ServerTaskRequested", cut.Markup);
        Assert.DoesNotContain("/tasks/5000", cut.Markup);
    }

    [Fact]
    public async Task ATaskAlreadyRunningBeforeTheClick_IsNotTheOneFollowed()
    {
        var cut = RenderFollower();
        await cut.InvokeAsync(() => Tracker.Upsert(new ServerTaskDto
        {
            Id = 50004,
            ServerId = 5,
            Name = "RKHunter - UpdateDatabase",
            Status = TaskExecutionStatus.Running
        }));
        await cut.InvokeAsync(() => cut.Instance.Expect("RunScan"));

        await cut.InvokeAsync(() => Tracker.HandleTerminal(new TaskCompletedNotification
        {
            TaskId = 50004,
            ServerId = 5,
            TaskName = "RKHunter - UpdateDatabase",
            Status = TaskExecutionStatus.Success
        }));

        Assert.Contains("ServerTaskRequested", cut.Markup);
        Assert.DoesNotContain("ServerTaskSucceeded", cut.Markup);
    }
}
