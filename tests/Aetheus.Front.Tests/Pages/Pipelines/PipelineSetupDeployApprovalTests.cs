// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-60 (user decision 2026-11-03): a production deployment created by the wizard always asks for an approval,
/// even for a release graded A. The option "Ask my approval before the deployment to production" is offered when the
/// deployment pipeline is created, ticked by default, written into that pipeline as the variable the template's approval stage
/// reads, said in the summary, and its choice survives the summary's address (an address without it reopens ticked).</summary>
public sealed class PipelineSetupDeployApprovalTests : BunitContext
{
    private const string Tile = "setup-option-deploy-approval";
    private const string Group = "[data-testid='setup-option-deploy-approval']";
    private const string Variable = "APPLICATION_DEPLOY_REQUIRE_APPROVAL";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupDeployApprovalTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void Arrange()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync()
    {
        Arrange();
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        return cut;
    }

    private IRenderedComponent<PipelineSetupWizard> OpenSummaryFrom(string options)
    {
        Arrange();
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"http://localhost/pipelines/setup?projectId=7&prefix=atlas&options={Uri.EscapeDataString(options)}"
            + "&architecture=application&sameProject=no&step=summary");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("atlas-deploy-prod", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static Task ClickAsync(IRenderedComponent<PipelineSetupWizard> cut, string testId) =>
        cut.Find($"[data-testid='{testId}']").ClickAsync(new MouseEventArgs());

    private async Task<CreatePipelineRequest[]> FinishAsync(IRenderedComponent<PipelineSetupWizard> cut, int expected, bool fromCapabilities)
    {
        if (fromCapabilities)
        {
            await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
            cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        }
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(expected, Created().Length));
        return Created();
    }

    private CreatePipelineRequest[] Created() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

    private static string DeployYaml(CreatePipelineRequest[] created) =>
        Assert.Single(created, request => request.Name == "atlas-deploy-prod").YamlDefinition;

    [Fact]
    public async Task TheOption_IsOfferedTickedByDefault_AndTheDeploymentPipelineAsksForTheApproval()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.NotEmpty(cut.FindAll(Group));
        Assert.Equal("true", cut.Find($"[data-testid='{Tile}']").GetAttribute("aria-checked"));
        var created = await FinishAsync(cut, 7, fromCapabilities: true);

        Assert.Contains($"{Variable}: \"true\"", DeployYaml(created), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOptionUnticked_WritesFalse_ButStillCreatesTheDeploymentPipeline()
    {
        var cut = await OpenCapabilitiesAsync();

        await ClickAsync(cut, Tile);
        var created = await FinishAsync(cut, 7, fromCapabilities: true);

        Assert.Contains($"{Variable}: \"false\"", DeployYaml(created), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOption_IsGreyed_WhenTheDeploymentPipelineIsNotCreated_AndOtherPipelinesCarryNoApprovalVariable()
    {
        var cut = await OpenCapabilitiesAsync();

        await ClickAsync(cut, "setup-option-production");

        // R-23: greyed rather than removed.
        Assert.Equal("true", cut.Find($"[data-testid='{Tile}']").GetAttribute("aria-disabled"));
        var created = await FinishAsync(cut, 6, fromCapabilities: true);
        Assert.All(created, request => Assert.DoesNotContain(Variable, request.YamlDefinition, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSummary_SaysTheApprovalIsAsked()
    {
        var cut = await OpenCapabilitiesAsync();
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));

        Assert.Contains("PipelineSetupRecapApprovalAsked", cut.Find("[data-testid='setup-recap-approval']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummary_SaysTheApprovalIsNotAsked_WhenTheOptionIsUnticked()
    {
        var cut = await OpenCapabilitiesAsync();
        await ClickAsync(cut, Tile);
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));

        Assert.Contains("PipelineSetupRecapApprovalNotAsked", cut.Find("[data-testid='setup-recap-approval']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUntickedOption_TravelsInTheSummaryAddress()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = await OpenCapabilitiesAsync();

        await ClickAsync(cut, Tile);
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);

        Assert.Contains(PipelineSetupDeployApproval.SkipOption, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATickedOption_AddsNothingToTheSummaryAddress()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = await OpenCapabilitiesAsync();

        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);

        Assert.Contains("step=summary", nav.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain(PipelineSetupDeployApproval.SkipOption, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummaryAddress_WithTheOptionUnticked_ReopensUnticked_AndWithoutItReopensTicked()
    {
        var unticked = OpenSummaryFrom($"backend,database,ci,candidate,production,{PipelineSetupDeployApproval.SkipOption}");
        var withoutApproval = await FinishAsync(unticked, 3, fromCapabilities: false);

        Assert.Contains($"{Variable}: \"false\"", DeployYaml(withoutApproval), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASummaryAddressWithoutTheOption_ReopensTicked_AndWritesTheApproval()
    {
        var cut = OpenSummaryFrom("backend,database,ci,candidate,production");

        var created = await FinishAsync(cut, 3, fromCapabilities: false);

        Assert.Contains($"{Variable}: \"true\"", DeployYaml(created), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuilder_WritesTheApprovalVariableOnTheDeploymentPipelineOnly_TrueByDefault()
    {
        var input = new PipelineSetupYamlInput(
            "atlas", (true, true, false), "package-web-analytics-dotnet",
            Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false,
            ["ci", "candidate", "deploy-prod"]);

        var deploy = PipelineSetupYamlBuilder.Build("atlas-deploy-prod", "application-deploy-prod", 3, input);

        Assert.Contains($"{Variable}: \"true\"", deploy, StringComparison.Ordinal);
        Assert.Contains($"{Variable}: \"false\"", PipelineSetupYamlBuilder.Build("atlas-deploy-prod", "application-deploy-prod", 3,
            input with { AskApprovalBeforeProduction = false }), StringComparison.Ordinal);
        Assert.DoesNotContain(Variable, PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3, input), StringComparison.Ordinal);
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back"
        }.Select(name => new PipelineTemplateSummaryDto
        {
            Id = name.GetHashCode(StringComparison.Ordinal),
            Name = name,
            Version = 1
        })
    ];
}
