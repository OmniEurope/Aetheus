// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-9: the "attention, il manque ça" alert names the variables nothing defines, on the project's
/// overview and on the page of the pipeline, as an alert and with the page that defines them.</summary>
public sealed class UndefinedVariablesAlertTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public UndefinedVariablesAlertTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    private static PipelineSetupReadinessDto Verdict(params PipelineSetupReadinessCheckDto[] checks) => new()
    {
        RepositoryInspected = true,
        InspectedBranch = "main",
        Checks = [.. checks]
    };

    private static PipelineSetupReadinessCheckDto Undefined(params string[] names) => new()
    {
        Kind = PipelineSetupReadinessKind.UndefinedVariables,
        Severity = PipelineSetupReadinessSeverity.Blocking,
        Items = [.. names],
        UndefinedVariables = [.. names.Select(name => new PipelineSetupReadinessUndefinedVariableDto
        {
            Name = name, Container = PipelineSetupReadinessContainerKind.Library, OwnerId = 31, OwnerName = "w5.share"
        })]
    };

    private static PipelineSetupReadinessCheckDto OtherFinding() => new()
    {
        Kind = PipelineSetupReadinessKind.MissingEnvironments,
        Severity = PipelineSetupReadinessSeverity.Blocking,
        Items = ["production"]
    };

    [Fact]
    public void TheProjectOverview_NamesTheMissingVariables_InAnAlert_WithoutTheOtherFindings()
    {
        _handler.SetJsonResponse("api/pipelines/setup/project-readiness?projectId=5", Verdict(
            Undefined("APPNAME", "DB_USER", "DEPLOY_ENV"), OtherFinding()));

        var cut = Render<ProjectPipelinesReadiness>(parameters => parameters
            .Add(panel => panel.ProjectId, 5)
            .Add(panel => panel.EmptyValuesOnly, true));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-readiness-check']")));
        Assert.Contains("ReadinessUndefinedVariables", cut.Markup, StringComparison.Ordinal);
        foreach (var name in new[] { "APPNAME", "DB_USER", "DEPLOY_ENV" })
            Assert.Contains(name, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("production", cut.Markup, StringComparison.Ordinal);
        // R-56: a table of the variables and their holder replaces the one generic link to the libraries page.
        Assert.Equal(3, cut.FindAll("[data-testid='setup-readiness-undefined-name']").Count);
        Assert.DoesNotContain("/projects/5/libraries", cut.FindAll("a").Select(link => link.GetAttribute("href")));
    }

    [Fact]
    public void ThePipelinePage_AsksTheVerdictOfThatPipeline_AndShowsOnlyItsMissingVariables()
    {
        _handler.SetJsonResponse("api/pipelines/setup/pipeline-readiness?pipelineId=21", Verdict(
            Undefined("APPNAME"), OtherFinding()));

        var cut = Render<ProjectPipelinesReadiness>(parameters => parameters
            .Add(panel => panel.ProjectId, 5)
            .Add(panel => panel.PipelineId, 21));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-readiness-check']")));
        Assert.Contains("APPNAME", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("production", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("project-readiness", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNothingMissing_NoAlertIsDrawn()
    {
        _handler.SetJsonResponse("api/pipelines/setup/pipeline-readiness?pipelineId=21", Verdict(OtherFinding()));

        var cut = Render<ProjectPipelinesReadiness>(parameters => parameters
            .Add(panel => panel.ProjectId, 5)
            .Add(panel => panel.PipelineId, 21));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("pipeline-readiness", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-check']"));
    }
}
