// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Tasks;
using Bunit;
using OmniEurope.Blazor.Components;
using TasksPage = Aetheus.Front.Components.Tasks.Tasks;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Render-path coverage for the Tasks list page with varied task statuses (template branches).</summary>
public class TasksRenderMarginTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public TasksRenderMarginTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PaginatedResult<ServerTaskDto> Tasks() => new()
    {
        Items =
        [
            new ServerTaskDto { Id = 1, ServerId = 7, Name = "deploy", Status = TaskExecutionStatus.Success, ServerName = "web-01", PipelineRunId = 42, Executor = ExecutorType.Shell, CreatedAt = DateTime.UtcNow.AddMinutes(-5), ExitCode = 0 },
            new ServerTaskDto { Id = 2, ServerId = 8, Name = "backup", Status = TaskExecutionStatus.Failed, ServerName = "db-01", Executor = ExecutorType.Docker, CreatedAt = DateTime.UtcNow.AddMinutes(-10), ExitCode = 1 },
            new ServerTaskDto { Id = 3, Name = "scan", Status = TaskExecutionStatus.Running, ServerName = "web-01", Executor = ExecutorType.Shell, CreatedAt = DateTime.UtcNow },
            new ServerTaskDto { Id = 4, Name = "rotate", Status = TaskExecutionStatus.Pending, ServerName = "db-01", Executor = ExecutorType.Shell, CreatedAt = DateTime.UtcNow },
            new ServerTaskDto { Id = 5, Name = "cleanup", Status = TaskExecutionStatus.Cancelled, ServerName = "web-01", Executor = ExecutorType.Shell, CreatedAt = DateTime.UtcNow }
        ],
        TotalCount = 5
    };

    [Fact]
    public void Renders_WithVariedStatuses_ShowsAllTaskRows()
    {
        _handler.SetJsonResponse("api/tasks", Tasks());
        var cut = Render<TasksPage>();

        // The grid's LoadData fires on first render → the rows (with their varied statuses) render.
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("deploy", cut.Markup);
            Assert.Contains("backup", cut.Markup);
            Assert.Contains("scan", cut.Markup);
            Assert.Equal("/pipelines/runs/42", cut.Find("a[href='/pipelines/runs/42']").GetAttribute("href"));
            Assert.Equal("/servers/8/overview", cut.Find("a[href='/servers/8/overview']").GetAttribute("href"));
        });
    }

    [Fact]
    public void Renders_EmptyTasks_ShowsNoTaskRows()
    {
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto> { Items = [], TotalCount = 0 });
        var cut = Render<TasksPage>();

        // Initial load settles to an empty grid: no task-name rows, but the filter chrome stays.
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));
        Assert.DoesNotContain("deploy", cut.Markup);
        Assert.DoesNotContain("backup", cut.Markup);
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Filter);
    }

    [Fact]
    public async Task OnLoadData_PopulatesTasks()
    {
        _handler.SetJsonResponse("api/tasks", Tasks());
        var cut = Render<TaskListView>();

        // OnLoadData must exist (renamed/removed → test fails instead of silently passing).
        var m = typeof(TaskListView).GetMethod("OnLoadData", Priv)
            ?? throw new InvalidOperationException("OnLoadData not found");
        await cut.InvokeAsync(async () => await (Task)m.Invoke(cut.Instance, [new GridLoadArgs { Skip = 0, Top = 25 }])!);

        // The handler populates _tasks and _totalCount from the API result.
        var tasks = (List<ServerTaskDto>)typeof(TaskListView).GetField("_tasks", Priv)!.GetValue(cut.Instance)!;
        var total = (int)typeof(TaskListView).GetField("_totalCount", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(5, tasks.Count);
        Assert.Equal(5, total);
        Assert.Contains(tasks, t => t.Name == "deploy");
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Success, OmniTone.Success)]
    [InlineData(TaskExecutionStatus.Failed, OmniTone.Danger)]
    [InlineData(TaskExecutionStatus.Timeout, OmniTone.Danger)]
    [InlineData(TaskExecutionStatus.Running, OmniTone.Accent)]
    [InlineData(TaskExecutionStatus.Pending, OmniTone.Neutral)]
    [InlineData(TaskExecutionStatus.Cancelled, OmniTone.Warning)]
    [InlineData(TaskExecutionStatus.Assigned, OmniTone.Neutral)]
    public void GetTaskBadge_AllStatuses(TaskExecutionStatus status, OmniTone expected)
    {
        Assert.Equal(expected, TaskListView.GetTaskBadge(status));
    }
}
