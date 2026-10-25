// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

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
    public void TheRunPage_ShowsTheReleaseItCreated_ShortAndLinked()
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
        // R1-12: the timeline sits in the zone OE's veil covers while a report is published.
        var timeline = cut.Find(".run-overview > .run-timeline-zone");
        Assert.NotNull(timeline.QuerySelector(".run-tree-scroll"));
    }

    /// <summary>R1-12: while an analysis report is published for the run, the status tile says so and
    /// OE's veil lies over the timeline, which stays mounted; both go when the publication ends.</summary>
    [Fact]
    public async Task WhileAReportIsPublished_TheRunPageSaysSo_AndVeilsTheTimelineWithoutUnmountingIt()
    {
        var hubs = new Aetheus.Front.Tests.TestDoubles.InMemoryHubConnectionFactory(
            new AuthStateProvider(new Aetheus.Front.Tests.Services.FakeJsRuntime(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthStateProvider>.Instance));
        Services.AddSingleton<HubConnectionFactory>(hubs);
        _handler.SetJsonResponse("api/pipelines/runs/2421", Run(new()));
        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 2421));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".run-overview-tiles")));
        Assert.Empty(cut.FindAll(".run-analysis-publishing-badge"));
        Assert.Empty(cut.FindAll(".run-timeline-zone.omni-busy"));
        await hubs.Server("pipelines").WhenInvokedAsync("JoinPipelineRunGroup");

        await cut.InvokeAsync(() => hubs.Server("pipelines").PushAsync("AnalysisPublishing", 2421, true));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".run-analysis-publishing-badge")));
        var zone = cut.Find(".run-timeline-zone");
        Assert.Contains("omni-busy", zone.ClassName, StringComparison.Ordinal);
        Assert.Equal("true", zone.GetAttribute("aria-busy"));
        Assert.NotNull(zone.QuerySelector(".run-tree-scroll"));
        Assert.Contains("AnalysisPublishing", cut.Find(".run-analysis-publishing-badge").TextContent, StringComparison.Ordinal);

        await cut.InvokeAsync(() => hubs.Server("pipelines").PushAsync("AnalysisPublishing", 2421, false));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".run-analysis-publishing-badge")));
        Assert.DoesNotContain("omni-busy", cut.Find(".run-timeline-zone").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_33_TheCompatibilityAttestationBlock_IsNoLongerShownOnTheRunPage()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2421", Run(new()
        {
            ["COMPATIBILITY_MODE"] = "NMinusOne",
            ["PREVIOUS_VERSION_TESTED"] = PreviousSha,
            ["COMPATIBILITY_VERDICT"] = "NMinusOneVerified"
        }));

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 2421));

        cut.WaitForAssertion(() => Assert.Contains("aetheus-candidate", cut.Markup));
        Assert.Empty(cut.FindAll("[data-testid='attestation-mode']"));
        Assert.DoesNotContain("CompatibilityAttestation", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_30_ADeployRun_ShowsTheGateGradeOfTheDeployedCandidate_InItsStatusTile()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2819", Run(new()) with
        {
            Id = 2819,
            PipelineName = "deploy-prod",
            GateGrade = AnalysisGrade.A,
            Parameters = new Dictionary<string, string> { ["candidateVersion"] = "c-2800" }
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 2819));

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find(".run-overview-tiles [data-testid='run-deployed-candidate-gate']");
            Assert.Contains("RunDeployedCandidateGate", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("grade-badge--a", row.InnerHtml, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void R1_30_ARunThatDeploysNoCandidate_ShowsNoGateGradeInItsStatusTile()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2820", Run(new()) with { Id = 2820, GateGrade = AnalysisGrade.A });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 2820));

        cut.WaitForAssertion(() => Assert.Contains("aetheus-candidate", cut.Markup));
        Assert.Empty(cut.FindAll("[data-testid='run-deployed-candidate-gate']"));
    }
}
