// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-72 (user decision 2026-10-25): the candidate starts the deployment pipeline created in the same pass
/// only when the wizard's option "Run the deployment after the candidate" is ticked. It is ticked by default, offered only
/// when both pipelines are created, and its choice survives the summary's address (an address without it reopens ticked).</summary>
public sealed class PipelineSetupDeployChainTests : BunitContext
{
    private const string Tile = "setup-option-deploy-after-candidate";
    private const string Group = "[data-testid='setup-deploy-chain']";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupDeployChainTests() => _handler = BunitTestHelper.RegisterServices(this);

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

    private static string CandidateYaml(CreatePipelineRequest[] created) =>
        Assert.Single(created, request => request.Name == "atlas-candidate").YamlDefinition;

    [Fact]
    public async Task TheOption_IsOfferedTickedByDefault_AndTheCandidateIsChained()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.Equal("true", cut.Find($"[data-testid='{Tile}']").GetAttribute("aria-checked"));
        var created = await FinishAsync(cut, 7, fromCapabilities: true);

        Assert.Contains("on_success:", CandidateYaml(created), StringComparison.Ordinal);
        Assert.Contains("pipeline: atlas-deploy-prod", CandidateYaml(created), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOptionUnticked_WritesNoChain_ButStillCreatesTheDeployPipeline()
    {
        var cut = await OpenCapabilitiesAsync();

        await ClickAsync(cut, Tile);
        var created = await FinishAsync(cut, 7, fromCapabilities: true);

        Assert.DoesNotContain("on_success", CandidateYaml(created), StringComparison.Ordinal);
        Assert.Contains(created, request => request.Name == "atlas-deploy-prod");
    }

    [Fact]
    public async Task TheOption_IsHidden_WhenTheDeploymentPipelineIsNotCreated()
    {
        var cut = await OpenCapabilitiesAsync();
        Assert.NotEmpty(cut.FindAll(Group));

        await ClickAsync(cut, "setup-option-production");

        Assert.Empty(cut.FindAll(Group));
        Assert.Empty(cut.FindAll($"[data-testid='{Tile}']"));
    }

    [Fact]
    public async Task TheOption_IsHidden_WhenNeitherTheCandidateNorTheNightlyIsCreated()
    {
        var cut = await OpenCapabilitiesAsync();

        await ClickAsync(cut, "setup-option-candidate");
        Assert.NotEmpty(cut.FindAll(Group));
        await ClickAsync(cut, "setup-option-nightly");

        Assert.Empty(cut.FindAll(Group));
    }

    [Fact]
    public async Task AnUntickedOption_TravelsInTheSummaryAddress()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = await OpenCapabilitiesAsync();

        await ClickAsync(cut, Tile);
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);

        Assert.Contains(PipelineSetupDeployChain.SkipOption, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATickedOption_AddsNothingToTheSummaryAddress()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = await OpenCapabilitiesAsync();

        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);

        Assert.Contains("step=summary", nav.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain(PipelineSetupDeployChain.SkipOption, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummaryAddress_WithTheOptionUnticked_ReopensUntickedAndWritesNoChain()
    {
        var cut = OpenSummaryFrom($"backend,database,ci,candidate,production,{PipelineSetupDeployChain.SkipOption}");

        var created = await FinishAsync(cut, 3, fromCapabilities: false);

        Assert.DoesNotContain("on_success", CandidateYaml(created), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASummaryAddressWithoutTheOption_ReopensTicked_AndWritesTheChain()
    {
        var cut = OpenSummaryFrom("backend,database,ci,candidate,production");

        var created = await FinishAsync(cut, 3, fromCapabilities: false);

        Assert.Contains("on_success:", CandidateYaml(created), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuilder_WritesTheChainOnlyWhenTheOptionIsOn()
    {
        var input = new PipelineSetupYamlInput(
            "atlas", (true, true, false), "package-web-analytics-dotnet",
            Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false,
            ["ci", "candidate", "deploy-prod"]);

        Assert.Contains("on_success:", PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3, input), StringComparison.Ordinal);
        Assert.DoesNotContain("on_success", PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3,
            input with { RunDeployAfterCandidate = false }), StringComparison.Ordinal);
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod"
        }.Select(name => new PipelineTemplateSummaryDto
        {
            Id = name.GetHashCode(StringComparison.Ordinal),
            Name = name,
            Version = 1
        })
    ];
}
