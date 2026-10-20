// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectTasksSectionTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectTasksSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerTaskDto MakeTask(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Status = TaskExecutionStatus.Success,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

    [Fact]
    public void Renders_WithTasks()
    {
        _handler.SetJsonResponse("api/projects/1/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [MakeTask(1, "Deploy Frontend"), MakeTask(2, "Deploy Backend")],
            TotalCount = 2
        });
        var cut = Render<ProjectTasksSection>(p =>
            p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Frontend"));

        // OnInitializedAsync fetches page 1 of the project's tasks and renders their names.
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/projects/1/tasks"));
        Assert.Contains("Deploy Frontend", cut.Markup);
        Assert.Contains("Deploy Backend", cut.Markup);
    }

    [Fact]
    public void ProjectIdChange_ReloadsTasksOnSameInstance()
    {
        _handler.SetJsonResponse("api/projects/1/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [MakeTask(1, "first-task")],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/projects/2/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [MakeTask(2, "second-task")],
            TotalCount = 1
        });
        var cut = Render<ProjectTasksSection>(p => p.Add(x => x.ProjectId, 1));

        cut.Render(p => p.Add(x => x.ProjectId, 2));

        cut.WaitForAssertion(() => Assert.Contains("second-task", cut.Markup));
        Assert.DoesNotContain("first-task", cut.Markup);
    }

    [Fact]
    public void Renders_WithNoTasks()
    {
        _handler.SetJsonResponse("api/projects/2/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [],
            TotalCount = 0
        });
        var cut = Render<ProjectTasksSection>(p =>
            p.Add(x => x.ProjectId, 2));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // Empty payload still resolves _result (grid renders) with zero items and shows the empty-state text.
        var result = (PaginatedResult<ServerTaskDto>?)typeof(ProjectTasksSection)
            .GetField("_result", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(result);
        Assert.Empty(result.Items);
        Assert.Contains("NoRecords", cut.Markup);
    }

    [Fact]
    public void Loading_InitiallyFalse()
    {
        _handler.SetJsonResponse("api/projects/3/tasks", new PaginatedResult<ServerTaskDto> { Items = [], TotalCount = 0 });
        var cut = Render<ProjectTasksSection>(p =>
            p.Add(x => x.ProjectId, 3));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var loading = (bool)typeof(ProjectTasksSection).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    [Fact]
    public void Page_InitiallyOne()
    {
        _handler.SetJsonResponse("api/projects/4/tasks", new PaginatedResult<ServerTaskDto> { Items = [], TotalCount = 0 });
        var cut = Render<ProjectTasksSection>(p =>
            p.Add(x => x.ProjectId, 4));
        var page = (int)typeof(ProjectTasksSection).GetField("_page", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(1, page);
    }

    [Fact]
    public async Task OnLoadDataAsync_LoadsRequestedPage()
    {
        _handler.SetJsonResponse("api/projects/5/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [MakeTask(1, "Task1")],
            TotalCount = 5
        });

        var cut = Render<ProjectTasksSection>(p =>
            p.Add(x => x.ProjectId, 5));
        cut.WaitForState(() => !(bool)typeof(ProjectTasksSection).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        _handler.SetJsonResponse(HttpMethod.Get, "page=2", new PaginatedResult<ServerTaskDto>
        {
            Items = [MakeTask(2, "Task2")],
            TotalCount = 5
        });

        var method = typeof(ProjectTasksSection).GetMethod("OnLoadDataAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 25, Top = 25 }])!);

        var page = (int)typeof(ProjectTasksSection).GetField("_page", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, page);
        var result = (PaginatedResult<ServerTaskDto>)typeof(ProjectTasksSection)
            .GetField("_result", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("Task2", Assert.Single(result.Items).Name);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_IsPopulated_AfterInit()
    {
        _handler.SetJsonResponse("api/projects/6/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [MakeTask(1, "Run Tests")],
            TotalCount = 1
        });
        var cut = Render<ProjectTasksSection>(p =>
            p.Add(x => x.ProjectId, 6));
        cut.WaitForState(() => !(bool)typeof(ProjectTasksSection).GetField("_loading", Priv)!.GetValue(cut.Instance)!);
        var result = (PaginatedResult<ServerTaskDto>?)typeof(ProjectTasksSection).GetField("_result", Priv)!.GetValue(cut.Instance);
        // OnInitializedAsync populates _result with the single seeded task.
        Assert.NotNull(result);
        Assert.Equal("Run Tests", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task HeaderFilters_AreColumnFilters_NotASearchTerm()
    {
        // Send the section header filters as endpoint column filters.
        _handler.SetJsonResponse("api/projects/1/tasks/filter-values", new TaskFilterValuesDto { ServerNames = ["build-01"] });
        _handler.SetJsonResponse("api/projects/1/tasks", new PaginatedResult<ServerTaskDto> { Items = [MakeTask(1, "t")], TotalCount = 1 });
        var cut = Render<ProjectTasksSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("t"));
        var grid = cut.FindComponent<AetheusDataGrid<ServerTaskDto>>();
        var separator = Aetheus.Shared.Components.Shared.GridFilter.ListSeparator;

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(ServerTaskDto.ServerName), "build-01", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(ServerTaskDto.Status), $"Failed{separator}Success", OmniDataGridFilterOperator.In)
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/projects/1/tasks?", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=ServerName", StringComparison.Ordinal)
                && url.Contains("Filters[0].Value=build-01", StringComparison.Ordinal)
                && url.Contains("Filters[1].Field=Status", StringComparison.Ordinal)
                && !url.Contains("search=", StringComparison.Ordinal);
        }));
        var values = (TaskFilterValuesDto)typeof(ProjectTasksSection).GetField("_filterValues", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(["build-01"], values.ServerNames);
    }
}
