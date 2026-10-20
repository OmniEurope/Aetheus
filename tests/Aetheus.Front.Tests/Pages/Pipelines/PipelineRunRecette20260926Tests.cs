// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Covers release and compatibility tiles, explanatory labels and links to the
/// previous-version commit.</summary>
public sealed class PipelineRunRecette20260926Tests : BunitContext
{
    private const string PreviousSha = "9e1d0a3b5c7e9f1a2b4c6d8e0f1a3b5c7d9e332d";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunRecette20260926Tests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    private static PipelineRunDto Run(Dictionary<string, string> outputs) => new()
    {
        Id = 2421,
        PipelineId = 5,
        PipelineName = "aetheus-candidate",
        Status = PipelineStatus.Success,
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        CompletedAt = DateTime.UtcNow,
        Steps =
        [
            new PipelineStepRunDto
            {
                Id = 1, StageName = "QA", StepName = "Persist compatibility", Status = TaskExecutionStatus.Success,
                OutputVariables = outputs
            }
        ]
    };

    [Fact]
    public void TheRunPage_ShowsTheReleaseItCreated_ShortAndLinked_AndTheAttestationRightUnderTheTimeline()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2421", Run(new() { ["COMPATIBILITY_MODE"] = "NMinusOne" }));
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/2421", new List<ReleaseDto>
        {
            new() { Id = 88, Version = "c-" + PreviousSha, Status = ReleaseStatus.Published }
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 2421));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".run-overview-tiles a[href='/releases/88']")));
        var version = cut.Find(".run-overview-tiles a[href='/releases/88'] .release-version");
        Assert.Equal("c-9e1d0a3b", version.TextContent);
        Assert.Equal("c-" + PreviousSha, version.GetAttribute("title"));
        var timeline = cut.Find(".run-overview > .run-tree-scroll");
        Assert.Contains("run-attestation-card", timeline.NextElementSibling!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAttestation_ExplainsNMinusOne_AndLinksTheShortV1Commit()
    {
        var cut = Render<PipelineRunCompatibilityAttestation>(parameters => parameters
            .Add(component => component.Run, Run(new()
            {
                ["COMPATIBILITY_MODE"] = "NMinusOne",
                ["PREVIOUS_VERSION_TESTED"] = PreviousSha,
                ["COMPATIBILITY_VERDICT"] = "NMinusOneDegraded"
            }))
            .Add(component => component.RepoId, 3));

        var mode = cut.Find("[data-testid='attestation-mode']");
        Assert.Equal("CompatibilityModeValueNMinusOne", mode.QuerySelector(".run-attestation-value")!.TextContent);
        Assert.Equal("CompatibilityModeHintNMinusOne", mode.QuerySelector(".run-attestation-hint")!.TextContent);
        var link = cut.Find("[data-testid='attestation-previous'] .short-id a.short-id__text");
        Assert.Equal($"/git-repositories/3/commits/{PreviousSha}", link.GetAttribute("href"));
        Assert.Equal("9e1d0a3b", link.TextContent);
        Assert.Equal(PreviousSha, cut.Find("[data-testid='attestation-previous'] .short-id").GetAttribute("title"));
        var verdict = cut.Find("[data-testid='attestation-verdict']");
        Assert.Contains("CompatibilityVerdictValueNMinusOneDegraded", verdict.TextContent, StringComparison.Ordinal);
        Assert.Equal("CompatibilityVerdictHintNMinusOneDegraded", verdict.QuerySelector(".run-attestation-hint")!.TextContent);
        Assert.DoesNotContain(">NMinusOne<", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAttestation_OfABootstrapRun_SaysThereIsNoV1_AndShowsAnUnknownCodeAsItIs()
    {
        var cut = Render<PipelineRunCompatibilityAttestation>(parameters => parameters
            .Add(component => component.Run, Run(new()
            {
                ["COMPATIBILITY_MODE"] = "Bootstrap",
                ["PREVIOUS_VERSION_TESTED"] = "false",
                ["COMPATIBILITY_VERDICT"] = "SomethingNew"
            })));

        Assert.Equal("CompatibilityModeHintBootstrap", cut.Find("[data-testid='attestation-mode'] .run-attestation-hint").TextContent);
        Assert.Equal("CompatibilityNoPreviousVersion", cut.Find("[data-testid='attestation-previous'] .run-attestation-value").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='attestation-previous'] a"));
        var verdict = cut.Find("[data-testid='attestation-verdict']");
        Assert.Contains("SomethingNew", verdict.TextContent, StringComparison.Ordinal);
        Assert.Empty(verdict.QuerySelectorAll(".run-attestation-hint"));
    }

    [Fact]
    public void ARunStoredWithTheScriptsPascalCaseNames_StillShowsItsAttestation()
    {
        var cut = Render<PipelineRunCompatibilityAttestation>(parameters => parameters
            .Add(component => component.Run, Run(new()
            {
                ["CompatibilityMode"] = "NMinusOne",
                ["PreviousVersionTested"] = PreviousSha,
                ["CompatibilityVerdict"] = "NMinusOneVerified"
            }))
            .Add(component => component.RepoId, 3));

        Assert.Equal("CompatibilityModeValueNMinusOne", cut.Find("[data-testid='attestation-mode'] .run-attestation-value").TextContent);
        Assert.Equal(
            $"/git-repositories/3/commits/{PreviousSha}",
            cut.Find("[data-testid='attestation-previous'] a.short-id__text").GetAttribute("href"));
        Assert.Contains("CompatibilityVerdictValueNMinusOneVerified", cut.Find("[data-testid='attestation-verdict']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUpperSnakeCaseName_WinsOverTheLegacyOne()
    {
        var cut = Render<PipelineRunCompatibilityAttestation>(parameters => parameters
            .Add(component => component.Run, Run(new()
            {
                ["CompatibilityMode"] = "NMinusOne",
                ["COMPATIBILITY_MODE"] = "Bootstrap",
                ["PREVIOUS_VERSION_TESTED"] = "false",
                ["CompatibilityVerdict"] = "NMinusOneDegraded",
                ["COMPATIBILITY_VERDICT"] = "CurrentVersionVerified"
            })));

        Assert.Equal("CompatibilityModeValueBootstrap", cut.Find("[data-testid='attestation-mode'] .run-attestation-value").TextContent);
        Assert.Contains("CompatibilityVerdictValueCurrentVersionVerified", cut.Find("[data-testid='attestation-verdict']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutACompatibilityMode_NoAttestationIsRendered()
    {
        var cut = Render<PipelineRunCompatibilityAttestation>(parameters => parameters
            .Add(component => component.Run, Run([])));

        Assert.Empty(cut.FindAll(".run-attestation-card"));
    }
}
