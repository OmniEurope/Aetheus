// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-62 and R-63: the pipeline name
/// leads to the pipeline, the status tile leads to the Gate tab when the run has a Gate.</summary>
public sealed class PipelineRunOverviewTilesTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunOverviewTilesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    private static PipelineRunDto Run(int id) => new()
    {
        Id = id,
        PipelineId = 5,
        PipelineName = "aetheus-candidate",
        Status = PipelineStatus.Success,
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        CompletedAt = DateTime.UtcNow,
        Steps = [new PipelineStepRunDto { Id = 1, StageName = "Build", StepName = "Build", Status = TaskExecutionStatus.Success }]
    };

    private void ServeGate(int id) =>
        _handler.SetJsonResponse(HttpMethod.Get, $"api/analysis/runs/{id}/result", new AnalysisRunGateDto
        {
            PipelineRunId = id,
            Status = AnalysisGateStatus.Passed,
            ReportCount = 1,
            Reports = [new AnalysisRunGateReportDto { ReportId = 9 }]
        });

    private IRenderedComponent<PipelineRun> RenderRun(int id) =>
        Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, id));

    [Fact]
    public void R62_ThePipelineName_IsALinkToThePipelinePage()
    {
        _handler.SetJsonResponse("api/pipelines/runs/3002", Run(3002));

        var cut = RenderRun(3002);

        var link = cut.WaitForElement(".run-id-tile [data-testid='run-pipeline-link']");
        Assert.Equal("a", link.TagName.ToLowerInvariant());
        Assert.Equal("aetheus-candidate", link.TextContent.Trim());
        Assert.StartsWith("/pipelines/5", link.GetAttribute("href"), StringComparison.Ordinal);
    }

    [Fact]
    public void R63_TheStatusTile_OpensTheGateTab_WhenTheRunHasAGate()
    {
        _handler.SetJsonResponse("api/pipelines/runs/3003", Run(3003));
        ServeGate(3003);

        var cut = RenderRun(3003);

        cut.WaitForAssertion(() => Assert.Contains("tab=gate", HrefOfStatusTile(cut), StringComparison.Ordinal));
    }

    [Fact]
    public void R63_TheStatusTile_StaysOnTheLogs_WhenTheRunHasNoGate()
    {
        _handler.SetJsonResponse("api/pipelines/runs/3004", Run(3004));

        var cut = RenderRun(3004);

        cut.WaitForAssertion(() => Assert.Contains("tab=logs", HrefOfStatusTile(cut), StringComparison.Ordinal));
        Assert.DoesNotContain("tab=gate", HrefOfStatusTile(cut), StringComparison.Ordinal);
    }

    private static string HrefOfStatusTile(IRenderedComponent<PipelineRun> cut) =>
        cut.Find(".run-overview-tiles .run-status-tile").GetAttribute("href") ?? string.Empty;
}
