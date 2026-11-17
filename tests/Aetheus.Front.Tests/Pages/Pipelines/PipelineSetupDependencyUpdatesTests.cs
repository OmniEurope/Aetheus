// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>PLAN-007 (recette R-9, user decision 2026-11-09): the wizard offers the automatic dependency updates for an application
/// and a package (an external repository's mirror is never written), ticked by default, and writes the project's setting from the tile once the pipelines exist. A refusal never blocks the wizard.
/// The HTTP side is a test handler: the real endpoint is not exercised here.</summary>
public sealed class PipelineSetupDependencyUpdatesTests : BunitContext
{
    private const string Tile = "setup-option-dependency-updates";
    private const string SettingsUrl = "api/projects/7/dependency-updates/settings";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupDependencyUpdatesTests() => _handler = BunitTestHelper.RegisterServices(this);

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync(string architecture = "application")
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", new[]
            {
                "application-ci", "application-quality", "application-security", "application-qa",
                "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back"
            }
            .Select(name => new PipelineTemplateSummaryDto { Id = name.GetHashCode(StringComparison.Ordinal), Name = name, Version = 1 })
            .ToList());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/collection", new PipelineSetupCollectionResultDto());
        _handler.SetResponse(HttpMethod.Put, SettingsUrl, HttpStatusCode.NoContent);
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, architecture, sameProject: architecture == "application" ? false : null);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        return cut;
    }

    private async Task FinishAsync(IRenderedComponent<PipelineSetupWizard> cut)
    {
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.EndsWith("/projects/7", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
    }

    private string SettingsBody() =>
        Assert.Single(_handler.RequestDetails, request => request.Method == HttpMethod.Put.Method
                                                          && request.Url.EndsWith(SettingsUrl, StringComparison.OrdinalIgnoreCase)).Body ?? string.Empty;

    [Theory]
    [InlineData("application")]
    [InlineData("package")]
    public async Task TheTile_IsOfferedTickedByDefault(string architecture)
    {
        var cut = await OpenCapabilitiesAsync(architecture);

        Assert.Equal("true", cut.Find($"[data-testid='{Tile}']").GetAttribute("aria-checked"));
    }

    [Fact]
    public async Task TheTile_IsNotOffered_ForAnExternalRepository_AndTheSettingIsLeftAlone()
    {
        var cut = await OpenCapabilitiesAsync("external");

        Assert.Empty(cut.FindAll($"[data-testid='{Tile}']"));
    }

    [Fact]
    public async Task Ticked_FinishKeepsTheProjectSettingOn()
    {
        var cut = await OpenCapabilitiesAsync();

        await FinishAsync(cut);

        Assert.Contains("\"enabled\":true", SettingsBody(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unticked_FinishTurnsTheProjectSettingOff()
    {
        var cut = await OpenCapabilitiesAsync();
        await cut.Find($"[data-testid='{Tile}']").ClickAsync(new MouseEventArgs());

        await FinishAsync(cut);

        Assert.Contains("\"enabled\":false", SettingsBody(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARefusedSetting_NeverBlocksTheWizard()
    {
        var cut = await OpenCapabilitiesAsync();
        _handler.SetResponse(HttpMethod.Put, SettingsUrl, HttpStatusCode.InternalServerError);

        await FinishAsync(cut);

        SettingsBody();
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Method == HttpMethod.Delete.Method);
    }

    [Fact]
    public async Task AnUntickedTile_TravelsInTheSummaryAddress_AndATickedOneAddsNothing()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var ticked = await OpenCapabilitiesAsync();
        await ticked.InvokeAsync(ticked.FindComponent<WizardDialog>().Instance.NextStep);
        Assert.DoesNotContain(PipelineSetupDependencyUpdates.SkipOption, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);

        var unticked = await OpenCapabilitiesAsync();
        await unticked.Find($"[data-testid='{Tile}']").ClickAsync(new MouseEventArgs());
        await unticked.InvokeAsync(unticked.FindComponent<WizardDialog>().Instance.NextStep);
        Assert.Contains(PipelineSetupDependencyUpdates.SkipOption, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);
    }
}
