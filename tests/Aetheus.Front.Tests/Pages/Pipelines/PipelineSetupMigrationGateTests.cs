// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-13 (user decision 2026-11-09): the wizard offers the expand/contract model for an application that has a
/// database, unticked by default; ticked, it turns on the project's migration gate once the pipelines exist, which is what makes the
/// project's expand/contract tab appear. A refusal never blocks the wizard. The HTTP side is a test handler: the real endpoint is not
/// exercised here.</summary>
public sealed class PipelineSetupMigrationGateTests : BunitContext
{
    private const string Tile = "setup-option-expand-contract";
    private const string GateUrl = "api/projects/7/migration-gate";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupMigrationGateTests() => _handler = BunitTestHelper.RegisterServices(this);

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync(string architecture = "application", bool database = true)
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
        _handler.SetResponse(HttpMethod.Put, "api/projects/7/dependency-updates/settings", HttpStatusCode.NoContent);
        _handler.SetResponse(HttpMethod.Put, GateUrl, HttpStatusCode.NoContent);
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, architecture, sameProject: architecture == "application" ? false : null);
        if (!database)
            await cut.Find("[data-testid='setup-option-database']").ClickAsync(new MouseEventArgs());
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

    private IEnumerable<(string Method, string Url, string? Body)> GateRequests() =>
        _handler.RequestDetails.Where(request => request.Method == HttpMethod.Put.Method
                                                 && request.Url.EndsWith(GateUrl, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task TheTile_IsOfferedUnticked_ForAnApplicationWithADatabase()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.Equal("false", cut.Find($"[data-testid='{Tile}']").GetAttribute("aria-checked"));
        Assert.Contains("PipelineSetupExpandContractHint", cut.Find($"[data-testid='{Tile}']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTile_IsNotOffered_WithoutADatabase_ForAPackage_OrAnExternalRepository()
    {
        Assert.Empty((await OpenCapabilitiesAsync(database: false)).FindAll($"[data-testid='{Tile}']"));
        Assert.Empty((await OpenCapabilitiesAsync("package")).FindAll($"[data-testid='{Tile}']"));
        Assert.Empty((await OpenCapabilitiesAsync("external")).FindAll($"[data-testid='{Tile}']"));
    }

    [Fact]
    public async Task Ticked_FinishTurnsTheProjectMigrationGateOn()
    {
        var cut = await OpenCapabilitiesAsync();
        await cut.Find($"[data-testid='{Tile}']").ClickAsync(new MouseEventArgs());

        await FinishAsync(cut);

        var request = Assert.Single(GateRequests());
        Assert.Contains("\"enabled\":true", request.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unticked_FinishWritesNothing_NeverTurningAnExistingSettingOff()
    {
        var cut = await OpenCapabilitiesAsync();

        await FinishAsync(cut);

        Assert.Empty(GateRequests());
    }

    [Fact]
    public async Task ARefusedSetting_NeverBlocksTheWizard()
    {
        var cut = await OpenCapabilitiesAsync();
        await cut.Find($"[data-testid='{Tile}']").ClickAsync(new MouseEventArgs());
        _handler.SetResponse(HttpMethod.Put, GateUrl, HttpStatusCode.InternalServerError);

        await FinishAsync(cut);

        Assert.Single(GateRequests());
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Method == HttpMethod.Delete.Method);
    }

    [Fact]
    public async Task ATickedTile_TravelsInTheSummaryAddress_AndAnUntickedOneAddsNothing()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var unticked = await OpenCapabilitiesAsync();
        await unticked.InvokeAsync(unticked.FindComponent<WizardDialog>().Instance.NextStep);
        Assert.DoesNotContain(PipelineSetupMigrationGate.Option, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);

        var ticked = await OpenCapabilitiesAsync();
        await ticked.Find($"[data-testid='{Tile}']").ClickAsync(new MouseEventArgs());
        await ticked.InvokeAsync(ticked.FindComponent<WizardDialog>().Instance.NextStep);
        Assert.Contains(PipelineSetupMigrationGate.Option, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);
    }
}
