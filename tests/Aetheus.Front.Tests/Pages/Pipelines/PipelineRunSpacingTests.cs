// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

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

    [Fact]
    public void TheOverviewTiles_FollowTheWidthOfTheirOwnGrid()
    {
        var cut = RenderRun(903, PipelineStatus.Success);

        var grid = cut.Find(".run-overview-tiles");
        // OE marks a grid that has a MinColumnWidth: its column count follows its own width, not the window.
        Assert.Contains("omni-tile-grid--fluid", grid.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverviewTiles_EachTakeTheHeightOfTheirOwnContent()
    {
        var cut = RenderRun(903, PipelineStatus.Success);

        // Owner decision of 2026-10-24 (recette R-15): no common height, the Quality tile no longer stretches
        // the others.
        Assert.Contains("omni-tile-grid--natural-heights", cut.Find(".run-overview-tiles").ClassName, StringComparison.Ordinal);
    }
}
