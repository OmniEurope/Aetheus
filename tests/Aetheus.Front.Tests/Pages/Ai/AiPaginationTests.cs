// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.AiTasks;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Ai;

public sealed class AiPaginationTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AiPaginationTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetJsonResponse("api/ai/consumption", new AiConsumptionDto());
        // The profile and model lists the header filters offer.
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>
        {
            new() { Id = 1, Name = "reviewer", Model = "claude-sonnet-5-5" },
            new() { Id = 2, Name = "auditor", Model = "claude-sonnet-5-5" }
        });
        _handler.SetJsonResponse("api/ai/tasks", new PaginatedResult<AiTaskDefinitionDto>
        {
            Items = [],
            TotalCount = 101,
            Page = 1,
            PageSize = 25
        });
        _handler.SetJsonResponse("api/ai/profiles", new PaginatedResult<AiRunnerProfileDto>
        {
            Items = [],
            TotalCount = 101,
            Page = 1,
            PageSize = 25
        });
        _handler.SetJsonResponse("api/ai/results", new PaginatedResult<AiRunResultDto>
        {
            Items =
            [
                new AiRunResultDto
                {
                    Id = 101,
                    ProfileName = "reviewer",
                    ReportMarkdown = "page five",
                    CreatedAt = DateTime.UtcNow
                }
            ],
            TotalCount = 101,
            Page = 5,
            PageSize = 25
        });
    }

    [Fact]
    public async Task TasksGrid_PageBeyondHundred_RequestsBackendPage()
    {
        var cut = Render<AiTasks>();

        await cut.InvokeAsync(() => cut.Instance.LoadDataAsync(
            new GridLoadArgs { Skip = 100, Top = 25 }));

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/ai/tasks", StringComparison.Ordinal)
            && request.Url.Contains("page=5", StringComparison.Ordinal)
            && request.Url.Contains("pageSize=25", StringComparison.Ordinal));
    }

    /// <summary>A header filter of the tasks grid reaches the API as a column filter.</summary>
    [Fact]
    public async Task TasksGrid_HeaderFilter_ReachesTheApi()
    {
        var cut = Render<AiTasks>();

        await cut.InvokeAsync(() => cut.Instance.LoadDataAsync(new GridLoadArgs
        {
            Skip = 0,
            Top = 25,
            Filters = [new GridFilterDescriptor("Enabled", "False", OmniDataGridFilterOperator.Equals)]
        }));

        Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("api/ai/tasks", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Enabled", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=False", StringComparison.Ordinal));
    }

    /// <summary>A header filter of the profiles grid reaches the API as a column filter.</summary>
    [Fact]
    public async Task ProfilesGrid_HeaderFilter_ReachesTheApi()
    {
        var cut = Render<AiRunnerProfiles>();

        await cut.InvokeAsync(() => cut.Instance.LoadDataAsync(new GridLoadArgs
        {
            Skip = 0,
            Top = 25,
            Filters = [new GridFilterDescriptor("Model", "claude", OmniDataGridFilterOperator.Equals)]
        }));

        Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("api/ai/profiles", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Model", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=claude", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "api/ai/tasks", "Enabled desc")]
    [InlineData(false, "api/ai/profiles", "Model desc")]
    public async Task GridSort_ReachesTheApi(bool tasks, string route, string orderBy)
    {
        if (tasks)
        {
            var cut = Render<AiTasks>();
            await cut.InvokeAsync(() => cut.Instance.LoadDataAsync(new GridLoadArgs
            {
                Skip = 0,
                Top = 25,
                OrderBy = orderBy
            }));
        }
        else
        {
            var cut = Render<AiRunnerProfiles>();
            await cut.InvokeAsync(() => cut.Instance.LoadDataAsync(new GridLoadArgs
            {
                Skip = 0,
                Top = 25,
                OrderBy = orderBy
            }));
        }

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains(route, StringComparison.Ordinal)
            && request.Url.Contains($"sortBy={orderBy.Split(' ')[0]}", StringComparison.Ordinal)
            && request.Url.Contains("sortDescending=true", StringComparison.Ordinal));
    }

    [Fact]
    public void TaskCompletion_FilterRejectsOrdinaryTasksAndAcceptsAiRuns()
    {
        Assert.False(AiTasks.ShouldReloadFor(new TaskCompletedNotification
        {
            Operation = OperationKind.None
        }));
        Assert.True(AiTasks.ShouldReloadFor(new TaskCompletedNotification
        {
            Operation = OperationKind.AiRun
        }));
    }

    [Fact]
    public void TasksGrid_RendersLinkedProjectAndServerSources()
    {
        _handler.SetJsonResponse("api/ai/tasks", new PaginatedResult<AiTaskDefinitionDto>
        {
            Items =
            [
                new AiTaskDefinitionDto { Id = 1, Name = "Project review", ProjectId = 4, ProjectName = "Website" },
                new AiTaskDefinitionDto { Id = 2, Name = "Server review", ServerId = 8, ServerName = "runner-08" }
            ],
            TotalCount = 2
        });

        var cut = Render<AiTasks>();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("a[href='/projects/4/overview']"));
            Assert.NotNull(cut.Find("a[href='/servers/8/overview']"));
        });
    }

    [Fact]
    public async Task ProfilesGrid_PageBeyondHundred_RequestsBackendPage()
    {
        var cut = Render<AiRunnerProfiles>();

        await cut.InvokeAsync(() => cut.Instance.LoadDataAsync(
            new GridLoadArgs { Skip = 100, Top = 25 }));

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/ai/profiles", StringComparison.Ordinal)
            && request.Url.Contains("page=5", StringComparison.Ordinal)
            && request.Url.Contains("pageSize=25", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResultsDialog_PageBeyondHundred_RequestsBackendPage()
    {
        var cut = Render<AiResultDialog>(
            parameters => parameters.Add(component => component.DefinitionId, 7));

        await cut.InvokeAsync(() => cut.Instance.OnPageChangedAsync(5));

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/ai/results", StringComparison.Ordinal)
            && request.Url.Contains("page=5", StringComparison.Ordinal)
            && request.Url.Contains("pageSize=25", StringComparison.Ordinal)
            && request.Url.Contains("definitionId=7", StringComparison.Ordinal));
    }
}
