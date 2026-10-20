// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Covers shared tab links, artifact-grid presentation and project-grouped coverage with
/// consistent gauge colours.</summary>
public sealed class PipelineRunRecette20260928Tests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunRecette20260928Tests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    private void MockCoverageEndpoints(int id)
    {
        _handler.SetJsonResponse($"api/pipelines/runs/{id}/coverage-trend", new List<CoverageTrendPointDto>());
    }

    private static PipelineRunDto Run(int id) => new()
    {
        Id = id,
        PipelineId = 5,
        PipelineName = "aetheus-candidate",
        Status = PipelineStatus.Success,
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        CompletedAt = DateTime.UtcNow,
        Steps =
        [
            new PipelineStepRunDto { Id = 1, StageName = "Build", StepName = "Build", Status = TaskExecutionStatus.Success }
        ]
    };

    private static readonly PipelineCoverageSummaryDto Coverage = new()
    {
        LineRate = 0.82,
        BranchRate = 0.4,
        LinesCovered = 82,
        LinesValid = 100,
        Files =
        [
            // Stored before the parser kept package names: the project is read from the path.
            new CoverageFileDto { File = "src/Aetheus.Back/Components/A.cs", LineRate = 0.5, LinesCovered = 10, LinesValid = 20 },
            new CoverageFileDto { File = "src/Aetheus.Back/Components/B.cs", LineRate = 1, LinesCovered = 30, LinesValid = 30 },
            new CoverageFileDto { Assembly = "Aetheus.Shared", File = "src/Aetheus.Shared/C.cs", LineRate = 0.84, LinesCovered = 42, LinesValid = 50 }
        ]
    };

    private IRenderedComponent<PipelineRun> RenderRun(int id, string? tab = null)
    {
        if (tab is not null)
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
                .NavigateTo($"/pipelines/runs/{id}?tab={tab}");
        return Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, id));
    }

    [Fact]
    public void Overview_HasNoCoverageTile_EvenWhenTheRunMeasuredCoverage()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2464", Run(2464) with { CoverageSummary = Coverage });
        MockCoverageEndpoints(2464);

        var cut = RenderRun(2464);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".run-overview-tiles")));
        Assert.DoesNotContain("Coverage", cut.Find(".run-overview-tiles").TextContent, StringComparison.Ordinal);
        // The coverage itself stays reachable, as a tab of the run.
        Assert.Contains("Coverage", cut.Find(".pipeline-run-tabs").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedGateLink_OpensTheGateTab_OnceTheGateHasLoaded()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2465", Run(2465));
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/2465/result", new AnalysisRunGateDto
        {
            PipelineRunId = 2465,
            Status = AnalysisGateStatus.Passed,
            ReportCount = 1,
            Reports = [new AnalysisRunGateReportDto { ReportId = 9 }]
        });

        var cut = RenderRun(2465, "gate");

        cut.WaitForAssertion(
            () => Assert.NotNull(cut.Find(".analysis-run-gate-page")));
    }

    [Fact]
    public void ArtifactsTab_IsTheGridAlone_WithoutACardOrACountTitle()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2466", Run(2466) with
        {
            Artifacts = [new PipelineArtifactDto { Id = 1, Name = "app.zip", SizeBytes = 1024, StageName = "package" }]
        });

        var cut = RenderRun(2466, "artifacts");

        var grid = cut.WaitForElement(".run-artifacts-grid");
        Assert.Null(grid.Closest(".omni-card"));
        Assert.DoesNotContain("Artifacts (1)", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageTab_GroupsTheFilesByProjectInATree()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2467", Run(2467) with { CoverageSummary = Coverage });
        MockCoverageEndpoints(2467);

        var cut = RenderRun(2467, "coverage");

        var tree = cut.WaitForElement(".coverage-project-tree");
        // The flat per-file table is gone.
        Assert.Empty(cut.FindAll(".coverage-file-name"));
        var projects = tree.Children.Where(child => child.GetAttribute("role") == "treeitem").ToList();
        Assert.Equal(2, projects.Count);
        // Collapsed, a project shows its line and not yet its files.
        Assert.Empty(cut.FindAll(".coverage-project-tree [title]"));

        // Least covered first: Aetheus.Back (40 / 50, read from the paths) before Aetheus.Shared
        // (42 / 50, its package); opened, it lists its own two files, the least covered first.
        cut.FindAll(".coverage-project-tree .omni-tree__toggle")[0].Click();

        var files = cut.FindAll(".coverage-project-tree [title]").Select(item => item.GetAttribute("title")).ToList();
        Assert.Equal(["src/Aetheus.Back/Components/A.cs", "src/Aetheus.Back/Components/B.cs"], files);
    }

    /// <summary>The run detail sends the coverage figures without the per-file list; the Coverage
    /// tab reads that list from the run's coverage route when it opens, and only then.</summary>
    [Fact]
    public void CoverageTab_ReadsTheFilesOnDemand_FromTheRunCoverageRoute()
    {
        var figures = Coverage with { Files = [], RunId = 2468 };
        _handler.SetJsonResponse("api/pipelines/runs/2468", Run(2468) with { CoverageSummary = figures });
        _handler.SetJsonResponse("api/pipelines/runs/2468/coverage", Coverage with { RunId = 2468 });
        MockCoverageEndpoints(2468);

        var cut = RenderRun(2468);
        cut.WaitForElement(".run-overview-tiles");
        Assert.DoesNotContain(_handler.Requests, request => request.Url.EndsWith("/runs/2468/coverage", StringComparison.Ordinal));

        // The reader opens the Coverage tab of the page already shown.
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/pipelines/runs/2468?tab=coverage");

        var tree = cut.WaitForElement(".coverage-project-tree");
        Assert.Equal(2, tree.Children.Count(child => child.GetAttribute("role") == "treeitem"));
        Assert.Single(_handler.Requests, request => request.Url.EndsWith("/runs/2468/coverage", StringComparison.Ordinal));
    }
}
