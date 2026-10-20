// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Tasks;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Tasks;

public class TasksDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public TasksDeepTests() => _handler = BunitTestHelper.RegisterServices(this);

    // The grid + filter + log logic moved into the shared TaskListView component, hosted by both
    // the global /tasks page and the per-server tasks section. Tests target the component directly.
    private IRenderedComponent<TaskListView> RenderView()
    {
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items =
            [
                new ServerTaskDto { Id = 1, Name = "Build", Status = TaskExecutionStatus.Success },
                new ServerTaskDto { Id = 2, Name = "Deploy", Status = TaskExecutionStatus.Failed }
            ],
            TotalCount = 2
        });
        return Render<TaskListView>();
    }

    // === GetTaskBadge (static) ===

    [Theory]
    [InlineData(TaskExecutionStatus.Success, OmniTone.Success)]
    [InlineData(TaskExecutionStatus.Failed, OmniTone.Danger)]
    [InlineData(TaskExecutionStatus.Timeout, OmniTone.Danger)]
    [InlineData(TaskExecutionStatus.Running, OmniTone.Accent)]
    [InlineData(TaskExecutionStatus.Cancelled, OmniTone.Warning)]
    [InlineData(TaskExecutionStatus.Pending, OmniTone.Neutral)]
    public void GetTaskBadge_ReturnsExpected(TaskExecutionStatus status, OmniTone expected)
    {
        Assert.Equal(expected, TaskListView.GetTaskBadge(status));
    }

    // === IsStalledOnOfflineAgent ===

    [Fact]
    public void IsStalledOnOfflineAgent_PendingOnOfflineServer_True()
    {
        var task = new ServerTaskDto { Id = 1, Status = TaskExecutionStatus.Pending, ServerStatus = ServerStatus.Offline };
        Assert.True(TaskListView.IsStalledOnOfflineAgent(task));
    }

    [Fact]
    public void IsStalledOnOfflineAgent_RunningOrOnline_False()
    {
        Assert.False(TaskListView.IsStalledOnOfflineAgent(
            new ServerTaskDto { Status = TaskExecutionStatus.Running, ServerStatus = ServerStatus.Offline }));
        Assert.False(TaskListView.IsStalledOnOfflineAgent(
            new ServerTaskDto { Status = TaskExecutionStatus.Pending, ServerStatus = ServerStatus.Online }));
    }

    // No filter row above the grid.

    [Fact]
    public void NoSearchBoxNorStatusList_AboveTheGrid()
    {
        // The Name and Status column headers provide the filters.
        var cut = RenderView();
        cut.WaitForState(() => cut.Markup.Contains("Build"));

        Assert.Empty(cut.FindAll(".task-list-toolbar"));
        Assert.Empty(cut.FindComponents<OmniDropDown<TaskExecutionStatus?>>());
        Assert.DoesNotContain("SearchTasks", cut.Markup);
        Assert.DoesNotContain("AllStatuses", cut.Markup);
        Assert.Null(typeof(TaskListView).GetField("_search", Priv));
        Assert.Null(typeof(TaskListView).GetField("_statusFilter", Priv));
    }

    // === Detail navigation ===

    [Fact]
    public void OpenTask_NavigatesToTaskDetail()
    {
        var cut = RenderView();

        cut.InvokeAsync(() => cut.Instance.OpenTask(new ServerTaskDto { Id = 2 }));

        Assert.EndsWith("/tasks/2", Services.GetRequiredService<NavigationManager>().Uri);
    }

    // Name and Status header filters.

    [Fact]
    public async Task NameAndStatusHeaderFilters_AreColumnFilters_WithoutSearchOrStatusParameters()
    {
        var cut = RenderView();
        var grid = cut.FindComponent<AetheusDataGrid<ServerTaskDto>>();

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(ServerTaskDto.Name), "deploy", OmniDataGridFilterOperator.Contains),
                new GridFilterDescriptor(nameof(ServerTaskDto.Status), "Failed", OmniDataGridFilterOperator.In)
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/tasks?", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=Name", StringComparison.Ordinal)
                && url.Contains("Filters[0].Value=deploy", StringComparison.Ordinal)
                && url.Contains("Filters[1].Field=Status", StringComparison.Ordinal)
                && url.Contains("Filters[1].Value=Failed", StringComparison.Ordinal)
                && !url.Contains("search=", StringComparison.Ordinal)
                && !url.Contains("status=", StringComparison.Ordinal);
        }));
    }

    // Header filters.

    [Fact]
    public async Task HeaderFilters_AreSentAsColumnFilters_AndTheServerListComesFromTheApi()
    {
        _handler.SetJsonResponse("api/tasks/filter-values", new TaskFilterValuesDto { ServerNames = ["web-1", "db-1"] });
        var cut = RenderView();
        var grid = cut.FindComponent<AetheusDataGrid<ServerTaskDto>>();
        var separator = Aetheus.Shared.Components.Shared.GridFilter.ListSeparator;

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(ServerTaskDto.ServerName), $"web-1{separator}db-1", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(ServerTaskDto.Executor), "Docker", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(ServerTaskDto.Status), $"Failed{separator}Timeout", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(ServerTaskDto.CreatedAt), "2026-09-01T08:00:00Z",
                    OmniDataGridFilterOperator.GreaterThanOrEquals, OmniDataGridFilterOperator.LessThan, "2026-09-01T12:00:00Z")
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/tasks?", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=ServerName", StringComparison.Ordinal)
                && url.Contains($"Filters[0].Value=web-1{separator}db-1", StringComparison.Ordinal)
                && url.Contains("Filters[1].Field=Executor", StringComparison.Ordinal)
                && url.Contains("Filters[2].Field=Status", StringComparison.Ordinal)
                && url.Contains("Filters[3].Field=CreatedAt", StringComparison.Ordinal)
                && url.Contains("Filters[3].SecondValue=2026-09-01T12:00:00Z", StringComparison.Ordinal);
        }));
        var values = (TaskFilterValuesDto)typeof(TaskListView).GetField("_filterValues", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(["web-1", "db-1"], values.ServerNames);
    }

    // === Render check ===

    [Fact]
    public void Renders_TaskList_WithMarkup()
    {
        var cut = RenderView();
        cut.WaitForState(() => cut.Markup.Contains("Build"));

        // The stubbed task rows render their names in the grid.
        Assert.Contains("Build", cut.Markup);
        Assert.Contains("Deploy", cut.Markup);
        Assert.Contains("href=\"/tasks/1\"", cut.Markup);
        Assert.DoesNotContain("receipt_long", cut.Markup);
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [new ServerTaskDto { Id = 1, Name = "Build", Status = TaskExecutionStatus.Success, ServerId = 3, PipelineRunId = 9 }],
            TotalCount = 1
        });
        var cut = Render<TaskListView>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // The task link, then the run and server links of its source.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2);
    }
}
