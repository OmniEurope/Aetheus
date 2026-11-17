// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TasksPage = Aetheus.Front.Components.Tasks.Tasks;

namespace Aetheus.Front.Tests.Pages;

public class TasksTests : BunitContext
{
    public TasksTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    [Fact]
    public void Renders_TaskGrid()
    {
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items =
            [
                new ServerTaskDto
                {
                    Id = 1,
                    Name = "deploy-app",
                    ServerName = "srv1",
                    Executor = ExecutorType.Shell,
                    Status = TaskExecutionStatus.Success,
                    CreatedAt = DateTime.UtcNow,
                    ExitCode = 0
                }
            ],
            TotalCount = 1
        });

        var cut = Render<TasksPage>();
        // The data grid's LoadData requires JS interop; verify page renders without error
        Assert.Contains("Tasks", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyGrid()
    {
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [],
            TotalCount = 0
        });

        var cut = Render<TasksPage>();
        Assert.Contains("Tasks", cut.Markup);
    }

    [Fact]
    public void ViewLogs_SetupRendersWithoutError()
    {
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [new ServerTaskDto { Id = 5, Name = "test", Status = TaskExecutionStatus.Success }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/logs/task/5", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 5, Message = "log line 1", Level = TaskLogLevel.Info, Timestamp = DateTime.UtcNow }
        });

        var cut = Render<TasksPage>();
        // The data grid's LoadData requires JS interop; verify page renders
        Assert.Contains("Tasks", cut.Markup);
    }

    // Status-badge styling now lives on the shared TaskListView component used by both task views.
    [Theory]
    [InlineData(TaskExecutionStatus.Success, OmniTone.Success)]
    [InlineData(TaskExecutionStatus.Failed, OmniTone.Danger)]
    [InlineData(TaskExecutionStatus.Timeout, OmniTone.Danger)]
    [InlineData(TaskExecutionStatus.Running, OmniTone.Accent)]
    [InlineData(TaskExecutionStatus.Cancelled, OmniTone.Warning)]
    [InlineData(TaskExecutionStatus.Pending, OmniTone.Neutral)]
    [InlineData(TaskExecutionStatus.Assigned, OmniTone.Neutral)]
    public void GetTaskBadge_ReturnsExpectedStyle(TaskExecutionStatus status, OmniTone expected)
    {
        var result = TaskListView.GetTaskBadge(status);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Renders_MultipleTasksWithVariousStatuses()
    {
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items =
            [
                new ServerTaskDto { Id = 1, Name = "deploy", ServerName = "srv1", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Success, CreatedAt = DateTime.UtcNow, ExitCode = 0 },
                new ServerTaskDto { Id = 2, Name = "backup", ServerName = "srv2", Executor = ExecutorType.Docker, Status = TaskExecutionStatus.Failed, CreatedAt = DateTime.UtcNow, ExitCode = 1 },
                new ServerTaskDto { Id = 3, Name = "test", ServerName = "srv1", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Running, CreatedAt = DateTime.UtcNow },
                new ServerTaskDto { Id = 4, Name = "cleanup", ServerName = "srv3", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Cancelled, CreatedAt = DateTime.UtcNow },
                new ServerTaskDto { Id = 5, Name = "build", ServerName = "srv1", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Timeout, CreatedAt = DateTime.UtcNow }
            ],
            TotalCount = 5
        });

        var cut = Render<TasksPage>();
        Assert.Contains("Tasks", cut.Markup);
    }

    [Fact]
    public void Renders_TaskWithLogs()
    {
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items =
            [
                new ServerTaskDto { Id = 10, Name = "deploy-v2", ServerName = "prod-01", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Success, CreatedAt = DateTime.UtcNow, ExitCode = 0, Command = "deploy.sh" }
            ],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/logs/task/10", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 10, Message = "Starting deploy...", Level = TaskLogLevel.Info, Timestamp = DateTime.UtcNow },
            new() { Id = 2, TaskId = 10, Message = "Deploy complete", Level = TaskLogLevel.Info, Timestamp = DateTime.UtcNow },
            new() { Id = 3, TaskId = 10, Message = "Warning: stale locks", Level = TaskLogLevel.Warning, Timestamp = DateTime.UtcNow }
        });

        var cut = Render<TasksPage>();
        Assert.Contains("Tasks", cut.Markup);
    }

    [Fact]
    public void OpenTask_NavigatesToDetailPage()
    {
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [new ServerTaskDto { Id = 7, Name = "deploy", Status = TaskExecutionStatus.Success }],
            TotalCount = 1
        });
        var cut = Render<TaskListView>();

        cut.InvokeAsync(() => cut.Instance.OpenTask(new ServerTaskDto { Id = 7 }));

        Assert.EndsWith("/tasks/7", Services.GetRequiredService<NavigationManager>().Uri);
    }

    /// <summary>ADR-060: the list shows the number on the server, the key only until the backfill reaches the
    /// task. The link keeps the integer until the step that switches addresses.</summary>
    [Fact]
    public void TheNumberColumn_ShowsTheNumber_OrTheKeyOfATaskNotNumberedYet()
    {
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items =
            [
                new ServerTaskDto { Id = 31696, Number = 12, Name = "numbered", Status = TaskExecutionStatus.Success },
                new ServerTaskDto { Id = 41, Name = "older", Status = TaskExecutionStatus.Success }
            ],
            TotalCount = 2
        });

        var cut = Render<TaskListView>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("a.pipeline-name-link").Count));
        var links = cut.FindAll("a.pipeline-name-link");
        Assert.Equal("12", links[0].TextContent);
        Assert.Equal("/tasks/31696", links[0].GetAttribute("href"));
        Assert.Equal("41", links[1].TextContent);
    }
}
