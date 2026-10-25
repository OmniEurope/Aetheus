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

    /// <summary>R-20: stages blocked because the production server parameter is empty are ONE message that
    /// names the parameter and says what to do; the stages sit folded under it and no template text shows.</summary>
    [Fact]
    public void StagesBlockedByAnEmptyServerParameter_AreOneMessage_WithWhatToDo_AndTheStagesFolded()
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
        Assert.Single(cut.FindAll("[data-testid='preflight-hint']"));
        Assert.Contains("PreflightEmptyParameterHint", cut.Find("[data-testid='preflight-hint']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", cut.Markup, StringComparison.Ordinal);
        // The three stages are listed, but inside a folded disclosure.
        var folded = cut.Find("[data-testid='preflight-stages']");
        var details = folded.TagName.Equals("DETAILS", StringComparison.OrdinalIgnoreCase) ? folded : folded.QuerySelector("details")!;
        Assert.False(details.HasAttribute("open"));
        Assert.Equal(3, folded.QuerySelectorAll(".omni-badge").Length);
        Assert.Contains("PreflightAffectedStages", folded.TextContent, StringComparison.Ordinal);
        // No page opens the parameter, so no action button is offered for it.
        Assert.DoesNotContain(cut.FindAll(".preflight-cause button"), button => button.TextContent.Contains("PreflightFix", StringComparison.Ordinal));
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
}
