// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>
/// The preflight preview now follows the trigger chain, so the blocked stages the dialog lists no
/// longer all belong to the pipeline the user is looking at. A bare "Deploy" badge on an
/// aetheus-candidate launch names a stage that pipeline does not have.
/// </summary>
public sealed class PreflightDialogChainLabelTests : BunitContext
{
    private static PreflightStageDto Blocked(string stageName, string? pipelineName, int depth) => new()
    {
        StageName = stageName,
        Resolved = false,
        Target = "prod-web",
        Reason = "no online matching agent",
        Depth = depth,
        PipelineName = pipelineName
    };

    private IRenderedComponent<PipelineRunPreflightDialog> Render(params PreflightStageDto[] unresolved)
    {
        BunitTestHelper.RegisterServices(this);
        return Render<PipelineRunPreflightDialog>(parameters => parameters
            .Add(dialog => dialog.Unresolved, unresolved));
    }

    [Fact]
    public void AStageOfATriggeredPipelineIsBadgedWithThatPipelineName()
    {
        var markup = Render(Blocked("Deploy", "aetheus-deploy-prod", depth: 1)).Markup;

        Assert.Contains("aetheus-deploy-prod / Deploy", markup, StringComparison.Ordinal);
    }

    /// <summary>R-63: stages blocked because the production server parameter is empty are ONE short sentence and
    /// one "choose the server" button: no hint, no thirteen badges, and never the "no agent matches" summary.</summary>
    [Fact]
    public void StagesBlockedByAnEmptyServerParameter_AreOneSentenceAndOneButton_WithoutTheStages()
    {
        PreflightStageDto Empty(string stage) => new()
        {
            StageName = stage,
            Resolved = false,
            Target = "deployServer",
            TargetKind = PreflightTargetKind.Agent,
            Reason = "Stage 'x': parameter 'deployServer' is empty, so no server is chosen for it.",
            EmptyTargetParameter = new PreflightEmptyParameterDto("deployServer", "Deploy server", "Serveur de production")
        };

        var cut = Render(Empty("Deploy"), Empty("Migrate"), Empty("Smoke"));

        var causes = cut.FindAll("[data-testid='preflight-cause']");
        Assert.Single(causes);
        Assert.Contains("PreflightEmptyParameterCause", causes[0].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='preflight-hint']"));
        Assert.DoesNotContain("${{", cut.Markup, StringComparison.Ordinal);
        // The stages are not listed, folded or not, and no "no agent matches" summary is shown.
        Assert.Empty(cut.FindAll("[data-testid='preflight-stages']"));
        Assert.Empty(cut.FindAll(".omni-badge"));
        Assert.DoesNotContain("PreflightBlockedSummary", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PreflightFix", cut.Markup, StringComparison.Ordinal);
        // The one action chooses the server.
        Assert.Single(cut.FindAll(".preflight-cause button"));
        Assert.Contains("PreflightChooseServer", cut.Find(".preflight-cause button").TextContent, StringComparison.Ordinal);
    }

    /// <summary>R-63: a mix keeps the "no agent matches" summary for the stages it is true of only.</summary>
    [Fact]
    public void TheNoAgentSummary_CountsOnlyTheStagesNoAgentServes()
    {
        var empty = new PreflightStageDto
        {
            StageName = "Deploy",
            Resolved = false,
            Target = "deployServer",
            TargetKind = PreflightTargetKind.Agent,
            Reason = "x",
            EmptyTargetParameter = new PreflightEmptyParameterDto("deployServer", "Deploy server", "Serveur de production")
        };

        var cut = Render(empty, Blocked("Migrate", null, 0));

        Assert.Contains("PreflightBlockedSummary", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("[data-testid='preflight-cause']").Count);
    }

    /// <summary>R-20: a target nobody serves is said once, in French, from what the stage targets, not by the
    /// backend's English sentence repeated per stage.</summary>
    [Fact]
    public void StagesWithoutAnOnlineAgent_ShareOneFrenchCause()
    {
        var cut = Render(Blocked("Deploy", null, 0), Blocked("Migrate", null, 0));

        Assert.Single(cut.FindAll("[data-testid='preflight-cause']"));
        Assert.Contains("PreflightCauseAgent", cut.Find("[data-testid='preflight-cause']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("no online matching agent", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='preflight-hint']"));
    }

    [Fact]
    public void AStageOfTheLaunchingPipelineKeepsItsBareName()
    {
        var cut = Render(Blocked("Deploy", pipelineName: null, depth: 0));

        Assert.Contains(cut.FindComponents<OmniBadge>(), badge => badge.Find("span").TextContent.Trim() == "Deploy");
        Assert.DoesNotContain(" / Deploy", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>R-113: a stage that names no agent is said in French as "no agent online", once for all such
    /// stages, never with the backend's raw placeholder (it used to read « (unspecified) »).</summary>
    [Fact]
    public void StagesNamingNoAgent_ShareOneFrenchCause_WithoutARawPlaceholder()
    {
        PreflightStageDto NoAgent(string stage) => new()
        {
            StageName = stage,
            Resolved = false,
            Target = string.Empty,
            TargetKind = PreflightTargetKind.Agent,
            Reason = $"Stage '{stage}': no online agent matches."
        };

        var cut = Render(NoAgent("Build"), NoAgent("Test"));

        Assert.Single(cut.FindAll("[data-testid='preflight-cause']"));
        Assert.Contains("PreflightCauseAnyAgent", cut.Find("[data-testid='preflight-cause']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("unspecified", cut.Markup, StringComparison.Ordinal);
    }
}
