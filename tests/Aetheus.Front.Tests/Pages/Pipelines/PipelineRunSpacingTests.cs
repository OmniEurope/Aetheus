// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R-14 and R-15 on the page of a run: the blocks of the page and of the overview are separated by the
/// gap of their stack, never by a margin carried by a block (R-14), and the overview tiles follow the width of
/// their own grid (R-15, OE <c>OmniTileGrid.MinColumnWidth</c>).
/// </summary>
public sealed class PipelineRunSpacingTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunSpacingTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    private IRenderedComponent<PipelineRun> RenderRun(int id, PipelineStatus status)
    {
        _handler.SetJsonResponse($"api/pipelines/runs/{id}", new PipelineRunDto
        {
            Id = id,
            PipelineId = 5,
            PipelineName = "spacing",
            Status = status,
            StartedAt = DateTime.UtcNow,
            RunToEnd = true,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1,
                    StepName = "Build",
                    StageName = "CI",
                    Status = TaskExecutionStatus.Failed,
                    FailureCode = TaskFailureCodes.BuildFailed,
                    FailureReason = "msbuild: exit 1"
                }
            ]
        });
        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, id));
        cut.WaitForState(() => cut.FindAll(".omni-tile-grid.run-overview-tiles").Count == 1);
        return cut;
    }

    private static bool CarriesAMargin(AngleSharp.Dom.IElement element) =>
        element.ClassList.Any(name => name.StartsWith("omni-u-mt-", StringComparison.Ordinal)
                                      || name.StartsWith("omni-u-mb-", StringComparison.Ordinal)
                                      || name.StartsWith("omni-u-my-", StringComparison.Ordinal));

    [Fact]
    public void ThePage_IsOneStack_AndNoneOfItsBlocksCarriesAMargin()
    {
        var cut = RenderRun(901, PipelineStatus.Failed);

        var page = cut.Find(".pipeline-run-page");
        Assert.Contains("omni-stack", page.ClassName, StringComparison.Ordinal);
        Assert.Contains("omni-stack--gap-md", page.ClassName, StringComparison.Ordinal);
        // The header, the alerts and the tabs are the stack's direct children; the tabs are one of them.
        Assert.Contains(page.Children, child => child.ClassList.Contains("pipeline-run-tabs"));
        Assert.Contains(page.Children, child => child.ClassList.Contains("omni-alert"));
        Assert.All(page.Children, child => Assert.False(CarriesAMargin(child), child.OuterHtml[..Math.Min(160, child.OuterHtml.Length)]));
    }

    [Fact]
    public void TheOverview_IsOneStack_AndNoneOfItsBlocksCarriesAMargin()
    {
        var cut = RenderRun(902, PipelineStatus.Failed);

        var overview = cut.Find(".run-overview");
        Assert.Contains("omni-stack", overview.ClassName, StringComparison.Ordinal);
        Assert.Contains("omni-stack--gap-md", overview.ClassName, StringComparison.Ordinal);
        Assert.Contains(overview.Children, child => child.ClassList.Contains("run-overview-tiles"));
        Assert.Contains(overview.Children, child => child.ClassList.Contains("run-failed-card"));
        Assert.All(overview.Children, child => Assert.False(CarriesAMargin(child), child.ClassName));
        // The folded stop reason of the failed step is spaced by its stack, not by a margin on the alert.
        Assert.False(CarriesAMargin(cut.Find(".run-failed-step [data-testid=step-stop-reason]")));
    }

    /// <summary>
    /// R-14: under the tab rail every tab starts at the same distance, the gap of the page stack (the panel's own
    /// padding, set in app.css). A tab whose first block carried <c>omni-u-mt-md</c> sat further from the rail than
    /// the overview, which has none.
    /// </summary>
    [Theory]
    [InlineData("coverage")]
    [InlineData("tests")]
    public void TheFirstBlockOfATab_CarriesNoMargin_SoEveryTabStartsAtTheSameGap(string tab)
    {
        var id = 910 + tab.Length;
        _handler.SetJsonResponse($"api/pipelines/runs/{id}", new PipelineRunDto
        {
            Id = id,
            PipelineId = 5,
            PipelineName = "spacing",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow,
            CoverageSummary = new PipelineCoverageSummaryDto
            {
                LineRate = 0.8,
                BranchRate = 0.5,
                LinesCovered = 80,
                LinesValid = 100,
                Files = [new CoverageFileDto { Assembly = "Aetheus.Shared", File = "src/Aetheus.Shared/C.cs", LineRate = 0.8, LinesCovered = 8, LinesValid = 10 }]
            },
            TestResultSummary = new PipelineTestResultSummaryDto { TotalTests = 4, Passed = 4 }
        });
        _handler.SetJsonResponse($"api/pipelines/runs/{id}/coverage-trend", new List<CoverageTrendPointDto>());
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/pipelines/runs/{id}?tab={tab}");

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, id));

        var panel = cut.WaitForElement(".pipeline-run-tabs > .omni-tabs__panel:not([hidden])");
        cut.WaitForAssertion(() => Assert.NotEmpty(panel.Children));
        Assert.All(panel.Children, child => Assert.False(CarriesAMargin(child), child.ClassName));
    }

    [Fact]
    public void TheOverviewTiles_FollowTheWidthOfTheirOwnGrid()
    {
        var cut = RenderRun(903, PipelineStatus.Success);

        var grid = cut.Find(".run-overview-tiles");
        // OE marks a grid that has a MinColumnWidth: its column count follows its own width, not the window.
        Assert.Contains("omni-tile-grid--fluid", grid.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void R45_TheOverviewTiles_ShareTheHeightOfTheTallest()
    {
        var cut = RenderRun(903, PipelineStatus.Success);

        // Owner decision of 2026-10-25 (recette R-45, replacing the R-15 one): every tile of the overview has the same
        // height; the Quality tile makes the others taller.
        Assert.DoesNotContain("omni-tile-grid--natural-heights", cut.Find(".run-overview-tiles").ClassName, StringComparison.Ordinal);
    }
}
