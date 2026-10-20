// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// The project overview's last release metrics (coverage and lines of code of the run behind the most
/// recent release that has one, shown only when the run holds a real figure) and its live recent pipelines
/// (a completed, cancelled or approval-changed run of this project reloads them, another project's does not).
/// </summary>
public sealed class ProjectOverviewSectionLiveTests : BunitContext
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectOverviewSectionLiveTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        this.UseInMemoryHubs();
        _handler.SetJsonResponse("api/pipelines/runs/recent?projectId=1", new List<PipelineRunDto>());
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False", []);
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items =
            [
                new ReleaseDto { Id = 3, Version = "1.3.0", DetectedAt = T0.AddDays(3) },
                new ReleaseDto { Id = 2, Version = "1.2.0", PublishedAt = T0.AddDays(2), DetectedAt = T0, PipelineRunId = 50 },
                new ReleaseDto { Id = 1, Version = "1.1.0", DetectedAt = T0.AddDays(1), PipelineRunId = 49 }
            ],
            TotalCount = 3
        });
    }

    private InMemoryHubServer Pipelines => this.Hub("pipelines");

    private IRenderedComponent<ProjectOverviewSection> RenderOverview() =>
        Render<ProjectOverviewSection>(parameters => parameters.Add(section => section.Project, new ProjectDetailDto { Id = 1, Name = "Web" }));

    private void ServeRun(int id, PipelineCoverageSummaryDto? coverage, params RunMetricDto[] metrics) =>
        _handler.SetJsonResponse(HttpMethod.Get, $"api/pipelines/runs/{id}", new PipelineRunDto
        {
            Id = id,
            ProjectId = 1,
            PipelineName = "candidate",
            Status = PipelineStatus.Success,
            CoverageSummary = coverage,
            Metrics = [.. metrics]
        });

    private Task RunReadAsync(int id) =>
        _handler.WhenRequestedAsync(
            request => request.Url.EndsWith($"api/pipelines/runs/{id}", StringComparison.Ordinal), 1, $"run {id} read");

    [Fact]
    public async Task TheRunBehindTheLatestReleaseWithOne_GivesItsCoverageAndLinesOfCode()
    {
        ServeRun(50, new PipelineCoverageSummaryDto { LineRate = 0.825 },
            new RunMetricDto { Key = "loc.total", Value = 12345.4 });

        var cut = RenderOverview();

        await cut.WhenRenderedAsync(() =>
        {
            Assert.Contains("LastReleaseMetrics", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("LinesOfCode", cut.Markup, StringComparison.Ordinal);
            Assert.Contains(">Coverage<", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("title=\"1.2.0\"", cut.Markup, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(_handler.Requests, request => request.Url.EndsWith("api/pipelines/runs/49", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LinesOfCodeAlone_AreShownWithoutACoverage()
    {
        ServeRun(50, null, new RunMetricDto { Key = "loc.total", Value = 900 });

        var cut = RenderOverview();

        await cut.WhenRenderedAsync(() => Assert.Contains("LinesOfCode", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(">Coverage<", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWithoutAnyFigure_ShowsNoMetrics()
    {
        ServeRun(50, null, new RunMetricDto { Key = "tests.total", Value = 12 });

        var cut = RenderOverview();

        await RunReadAsync(50);
        await cut.WhenRenderedAsync(() => Assert.Contains("NoRecentPipelines", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("LastReleaseMetrics", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatCannotBeRead_ShowsNoMetrics()
    {
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/runs/50", System.Net.HttpStatusCode.ServiceUnavailable);

        var cut = RenderOverview();

        await RunReadAsync(50);
        await cut.WhenRenderedAsync(() => Assert.Contains("NoRecentPipelines", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("LastReleaseMetrics", cut.Markup, StringComparison.Ordinal);
    }

    private int RecentReads() =>
        _handler.Requests.ToList().Count(request => request.Url.Contains("api/pipelines/runs/recent?projectId=1", StringComparison.Ordinal));

    private async Task RenderConnectedAsync()
    {
        ServeRun(50, null);
        RenderOverview();
        await Pipelines.WhenInvokedAsync("JoinPipelineUpdatesGroup");
        await _handler.WhenGetAsync("api/pipelines/runs/recent?projectId=1");
        Assert.Equal(1, Pipelines.Count("JoinPipelineUpdatesGroup"));
        Assert.Equal(1, RecentReads());
    }

    // Each push completes once the section's handler ran to its end (it reads the run, then reloads the
    // recent pipelines when the run is this project's), so its effect is read right after it.

    [Theory]
    [InlineData("PipelineRunCompleted")]
    [InlineData("PipelineRunCancelled")]
    [InlineData("ApprovalRequired")]
    [InlineData("ApprovalResolved")]
    public async Task AChangedRunOfThisProject_ReloadsTheRecentPipelines(string message)
    {
        await RenderConnectedAsync();
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/runs/77", new PipelineRunDto { Id = 77, ProjectId = 1 });

        await Push(message, 77);

        Assert.Equal(2, RecentReads());
    }

    [Fact]
    public async Task AnotherProjectsRun_OrARunThatCannotBeRead_ReloadsNothing()
    {
        await RenderConnectedAsync();
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/runs/78", new PipelineRunDto { Id = 78, ProjectId = 2 });
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/runs/79", System.Net.HttpStatusCode.Unauthorized);

        await Push("PipelineRunCompleted", 78);
        await Push("PipelineRunCompleted", 79);

        Assert.Contains(_handler.Requests.ToList(), request => request.Url.EndsWith("api/pipelines/runs/79", StringComparison.Ordinal));
        Assert.Equal(1, RecentReads());
    }

    private Task Push(string message, int runId) => message switch
    {
        "PipelineRunCompleted" => Pipelines.PushAsync(message, runId, PipelineStatus.Success),
        "PipelineRunCancelled" => Pipelines.PushAsync(message, runId),
        _ => Pipelines.PushAsync(message, runId, "stage", "approver")
    };
}
