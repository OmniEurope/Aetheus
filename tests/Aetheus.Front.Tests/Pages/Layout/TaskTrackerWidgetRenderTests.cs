// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Layout;

public class TaskTrackerWidgetRenderTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type WidgetType = typeof(TaskTrackerWidget);

    public TaskTrackerWidgetRenderTests()
    {
        BunitTestHelper.RegisterServices(this, authenticated: false);
        Services.AddScoped(sp => new TaskTrackerService(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<AuthStateProvider>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            NullLogger<TaskTrackerService>.Instance));
    }

    [Fact]
    public void Renders_WithZeroCount_ShowsCheckIcon_AndNoNumber()
    {
        var cut = Render<TaskTrackerWidget>();

        Assert.Single(cut.FindAll(".task-tracker-btn svg.header-icon"));
        Assert.Empty(cut.FindAll(".task-tracker-count"));
    }

    [Theory]
    [InlineData(3, "3")]
    [InlineData(120, "99+")]
    public void Renders_WithNonZeroCount_TheIconThenTheNumber_InAWiderButton(int count, string shown)
    {
        // Keep the task icon and widen the button when a count is present, displaying 99+ above 99.
        var cut = Render<TaskTrackerWidget>();
        var tracker = Services.GetRequiredService<TaskTrackerService>();
        var byId = typeof(TaskTrackerService)
            .GetField("_byId", Priv)!.GetValue(tracker) as Dictionary<int, ServerTaskDto>;
        for (var id = 1; id <= count; id++)
            byId![id] = new ServerTaskDto { Id = id, ServerId = 10, ServerName = "srv1", Name = "Deploy", Status = TaskExecutionStatus.Running, CreatedAt = DateTime.UtcNow, Command = "cmd", TimeoutSeconds = 60 };

        WidgetType.GetMethod("OnTrackerChanged", Priv)!.Invoke(cut.Instance, []);
        cut.Render();

        var button = cut.Find(".task-tracker-btn");
        Assert.Equal(shown, button.QuerySelector(".task-tracker-count")!.TextContent.Trim());
        Assert.NotNull(button.QuerySelector("svg.header-icon"));
        Assert.Contains("task-tracker-btn-wide", button.ClassList);
        Assert.DoesNotContain("task-tracker-badge", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("TaskTrackerTooltip", button.GetAttribute("title"), StringComparison.Ordinal);
    }

    [Fact]
    public void TriggerClick_OpensThenSecondClickClosesPanel()
    {
        var cut = Render<TaskTrackerWidget>();

        // Open
        cut.Find("button#task-tracker.task-tracker-btn").Click();
        Assert.Single(cut.FindAll("#task-tracker-panel"));
        Assert.Equal("true", cut.Find("button#task-tracker.task-tracker-btn").GetAttribute("aria-expanded"));

        // Close
        cut.Find("button#task-tracker.task-tracker-btn").Click();
        Assert.Empty(cut.FindAll("#task-tracker-panel"));
        Assert.Equal("false", cut.Find("button#task-tracker.task-tracker-btn").GetAttribute("aria-expanded"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DismissRequest_EscapeOrOutsideClick_ClosesPanel(bool fromKeyboard)
    {
        var cut = Render<TaskTrackerWidget>();
        cut.Find("button#task-tracker.task-tracker-btn").Click();
        Assert.Single(cut.FindAll("#task-tracker-panel"));

        var popover = cut.FindComponent<OmniPopover>();
        await cut.InvokeAsync(() => popover.Instance.OnDismissRequestedAsync(fromKeyboard));

        Assert.Empty(cut.FindAll("#task-tracker-panel"));
    }

    [Fact]
    public void PopoverOpen_ShowsEmptyState_WhenNoTasks()
    {
        var cut = Render<TaskTrackerWidget>();
        cut.Find("button#task-tracker.task-tracker-btn").Click();
        Assert.Single(cut.FindAll("#task-tracker-panel .task-tracker-empty"));
        Assert.Empty(cut.FindAll("#task-tracker-panel .task-tracker-row"));
    }

    [Fact]
    public void PopoverOpen_ShowsTaskRows_WhenHasTasks()
    {
        var cut = Render<TaskTrackerWidget>();
        var tracker = Services.GetRequiredService<TaskTrackerService>();
        var byId = typeof(TaskTrackerService).GetField("_byId", Priv)!.GetValue(tracker) as Dictionary<int, ServerTaskDto>;
        byId![1] = new ServerTaskDto { Id = 1, ServerId = 5, ServerName = "web-server", Name = "TestTask", Status = TaskExecutionStatus.Running, CreatedAt = DateTime.UtcNow, Command = "echo hello", TimeoutSeconds = 60 };
        byId[2] = new ServerTaskDto { Id = 2, ServerId = 5, ServerName = "web-server", Name = "Task2", Status = TaskExecutionStatus.Pending, CreatedAt = DateTime.UtcNow, Command = "sleep", TimeoutSeconds = 60 };
        byId[3] = new ServerTaskDto { Id = 3, ServerId = 6, ServerName = "db-server", Name = "BackupTask", Status = TaskExecutionStatus.Assigned, CreatedAt = DateTime.UtcNow, Command = "backup", TimeoutSeconds = 60 };

        // Force count update
        WidgetType.GetField("_count", Priv)!.SetValue(cut.Instance, 3);
        cut.Find("button#task-tracker.task-tracker-btn").Click();
        Assert.Equal(3, cut.FindAll("#task-tracker-panel .task-tracker-row").Count);
        Assert.Empty(cut.FindAll("#task-tracker-panel .task-tracker-group-header"));
    }

    [Fact]
    public void PopoverOpen_GroupsTasksByServer_WhenMoreThanFive()
    {
        var cut = Render<TaskTrackerWidget>();
        var tracker = Services.GetRequiredService<TaskTrackerService>();
        var byId = typeof(TaskTrackerService).GetField("_byId", Priv)!.GetValue(tracker) as Dictionary<int, ServerTaskDto>;
        for (int i = 1; i <= 6; i++)
        {
            byId![i] = new ServerTaskDto { Id = i, ServerId = i % 2 == 0 ? 10 : 20, ServerName = i % 2 == 0 ? "srv-a" : "srv-b", Name = $"Task{i}", Status = TaskExecutionStatus.Running, CreatedAt = DateTime.UtcNow, Command = "cmd", TimeoutSeconds = 60 };
        }
        WidgetType.GetField("_count", Priv)!.SetValue(cut.Instance, 6);
        cut.Find("button#task-tracker.task-tracker-btn").Click();
        Assert.Equal(2, cut.FindAll("#task-tracker-panel .task-tracker-group-header").Count);
        Assert.Equal(6, cut.FindAll("#task-tracker-panel .task-tracker-row-grouped").Count);
    }

    [Fact]
    public void OnTrackerChanged_UpdatesCount()
    {
        var cut = Render<TaskTrackerWidget>();
        WidgetType.GetField("_count", Priv)!.SetValue(cut.Instance, 0);

        // Push two live tasks into the tracker so a real recalculation moves _count off 0 - proving
        // OnTrackerChanged re-reads Tracker.Count rather than being a no-op on an empty tracker.
        var tracker = Services.GetRequiredService<TaskTrackerService>();
        var byId = typeof(TaskTrackerService).GetField("_byId", Priv)!.GetValue(tracker) as Dictionary<int, ServerTaskDto>;
        byId![1] = new ServerTaskDto { Id = 1, ServerId = 10, ServerName = "srv1", Name = "Deploy", Status = TaskExecutionStatus.Running, CreatedAt = DateTime.UtcNow, Command = "cmd", TimeoutSeconds = 60 };
        byId[2] = new ServerTaskDto { Id = 2, ServerId = 10, ServerName = "srv1", Name = "Backup", Status = TaskExecutionStatus.Pending, CreatedAt = DateTime.UtcNow, Command = "cmd", TimeoutSeconds = 60 };

        var method = WidgetType.GetMethod("OnTrackerChanged", Priv)!;
        method.Invoke(cut.Instance, []);

        var count = (int)WidgetType.GetField("_count", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, count);
    }

    [Fact]
    public void Dispose_Unsubscribes_WithoutThrowing()
    {
        var cut = Render<TaskTrackerWidget>();
        cut.Instance.Dispose();
    }
}
