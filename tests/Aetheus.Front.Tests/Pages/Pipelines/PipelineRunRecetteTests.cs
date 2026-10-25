// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-25, R-26, R-27 and R-29 on the pipeline run page.</summary>
public sealed class PipelineRunRecetteTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunRecetteTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    [Fact]
    public void R27_TheFirstTile_ShowsTheRunId_NotThePipelineDefinitionId_WithThePipelineNameUnder()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2836", new PipelineRunDto
        {
            Id = 2836,
            PipelineId = 24,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 2836));

        cut.WaitForAssertion(() =>
        {
            var tile = cut.Find(".run-id-tile");
            Assert.Contains("RunIdTile", tile.TextContent, StringComparison.Ordinal);
            // R-28: an OmniFactTile, the run id then the pipeline name as the values of its two rows.
            var values = tile.QuerySelectorAll(".omni-fact-tile__value").Select(value => value.TextContent.Trim()).ToList();
            Assert.Equal(["2836", "aetheus-candidate"], values);
        });
    }

    [Fact]
    public void R29_TheTitleStartsWithTheRunId_SoTheUsefulPartSurvivesTheMobileCut()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2836", new PipelineRunDto
        {
            Id = 2836,
            PipelineId = 24,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 2836));

        cut.WaitForAssertion(() =>
            Assert.StartsWith("#2836 ", cut.Find(".omni-page-header__title").TextContent.Trim(), StringComparison.Ordinal));
    }

    [Fact]
    public void R25_R26_TheAiResultsCard_TitlesItsHeaderBand_AndOffersAPrimaryEyeButton()
    {
        var result = new AiRunResultDto { Id = 1, ProfileName = "review", Verdict = AiVerdict.Pass, DurationMs = 125_000, Succeeded = true };

        var cut = Render<PipelineRunAiResultsCard>(parameters => parameters.Add(component => component.Results, [result]));

        cut.WaitForAssertion(() =>
        {
            // R-25: the title is in the card's own header band, apart from the body that holds the grid.
            var header = cut.Find(".omni-card > .omni-card__header");
            Assert.Contains("AiRunResults", header.TextContent, StringComparison.Ordinal);
            Assert.NotNull(cut.Find(".omni-card > .omni-card__body .omni-data-grid, .omni-card > .omni-card__body [role='grid'], .omni-card > .omni-card__body table"));
            Assert.Empty(cut.FindAll(".omni-card > .omni-card__body > strong"));
            // R-26: the only row action is the main action of the zone, so the primary (blue) variant.
            var eye = cut.Find("button[title='View']");
            Assert.Contains("omni-button--primary", eye.ClassName, StringComparison.Ordinal);
            // The duration reads like everywhere else.
            Assert.Contains("2min", cut.Markup, StringComparison.Ordinal);
        });
    }
}
