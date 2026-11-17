// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-47 (user decision 2026-11-03): the wizard proposes the telemetry of the application, ticked by default,
/// and configures it automatically when it is chosen, through the monitored application the deployment already reads. The
/// configuration never blocks the wizard: a refusal leaves the pipelines created and a warning. The choice survives the
/// summary's address (an address without it reopens ticked) and the summary says what happens.</summary>
public sealed class PipelineSetupTelemetryTests : BunitContext
{
    private const string Tile = "setup-option-telemetry";
    private const string ConfigureUrl = "api/pipelines/setup/collection";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupTelemetryTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void Arrange()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
        _handler.SetJsonResponse(HttpMethod.Post, ConfigureUrl, new PipelineSetupCollectionResultDto());
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync(string architecture = "application")
    {
        Arrange();
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, architecture, sameProject: architecture == "application" ? false : null);
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

    private async Task GoToSummaryAsync(IRenderedComponent<PipelineSetupWizard> cut)
    {
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
    }

    private async Task FinishAsync(IRenderedComponent<PipelineSetupWizard> cut, int pipelines)
    {
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(pipelines, _handler.RequestDetails.Count(request =>
            request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))));
    }

    private int ConfigureCalls() => _handler.RequestsSnapshot().Count(request =>
        request.Method == HttpMethod.Post.Method && request.Url.EndsWith(ConfigureUrl, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task TheTile_IsOfferedTickedByDefault_ForAnApplication()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.Equal("true", cut.Find($"[data-testid='{Tile}']").GetAttribute("aria-checked"));
        Assert.Equal("checkbox", cut.Find($"[data-testid='{Tile}']").GetAttribute("role"));
    }

    [Theory]
    [InlineData("package")]
    [InlineData("external")]
    public async Task TheTile_IsNotOffered_ForAPackageOrAnExternalRepository(string architecture)
    {
        var cut = await OpenCapabilitiesAsync(architecture);

        Assert.Empty(cut.FindAll($"[data-testid='{Tile}']"));
    }

    [Fact]
    public async Task WhenTicked_FinishConfiguresTheTelemetryOnce_ForTheProject_AfterThePipelinesAreCreated()
    {
        var cut = await OpenCapabilitiesAsync();
        await GoToSummaryAsync(cut);

        await FinishAsync(cut, 7);

        cut.WaitForAssertion(() => Assert.Equal(1, ConfigureCalls()));
        var requests = _handler.RequestsSnapshot();
        Assert.True(requests.FindLastIndex(request => request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
            < requests.FindIndex(request => request.Url.EndsWith(ConfigureUrl, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task WhenOnlyTheTelemetryIsUnticked_FinishAsksTheAudienceAlone()
    {
        var cut = await OpenCapabilitiesAsync();
        await ClickAsync(cut, Tile);
        await GoToSummaryAsync(cut);

        await FinishAsync(cut, 7);

        // R-26: the audience tile is ticked for an application with a front-end, so the call is made, without the telemetry.
        cut.WaitForAssertion(() => Assert.Equal(1, ConfigureCalls()));
        var body = _handler.RequestDetails.Single(request => request.Url.EndsWith(ConfigureUrl, StringComparison.OrdinalIgnoreCase)).Body;
        Assert.Contains("\"telemetry\":false", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"audience\":true", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenBothAreUnticked_FinishAsksNothing_AndTheWizardStillEndsOnTheOverview()
    {
        var cut = await OpenCapabilitiesAsync();
        await ClickAsync(cut, Tile);
        await ClickAsync(cut, "setup-option-audience");
        await GoToSummaryAsync(cut);

        await FinishAsync(cut, 7);

        cut.WaitForAssertion(() => Assert.EndsWith("/projects/7", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
        Assert.Equal(0, ConfigureCalls());
    }

    [Fact]
    public async Task ARefusedConfiguration_NeverBlocksTheWizard_ThePipelinesStayCreated_AndTheUserIsWarned()
    {
        var cut = await OpenCapabilitiesAsync();
        _handler.SetResponse(HttpMethod.Post, ConfigureUrl, HttpStatusCode.InternalServerError);
        await GoToSummaryAsync(cut);

        await FinishAsync(cut, 7);

        cut.WaitForAssertion(() => Assert.Equal(1, ConfigureCalls()));
        // No rollback: the pipelines created are never deleted because the telemetry could not be configured.
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Method == HttpMethod.Delete.Method);
        Assert.EndsWith("/projects/7", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummary_SaysTheTelemetryIsConfigured_WhenTicked_AndNotConfigured_WhenUnticked()
    {
        var cut = await OpenCapabilitiesAsync();
        await GoToSummaryAsync(cut);
        Assert.Contains("PipelineSetupRecapTelemetryOn", cut.Find("[data-testid='setup-recap-telemetry']").TextContent, StringComparison.Ordinal);

        var unticked = await OpenCapabilitiesAsync();
        await ClickAsync(unticked, Tile);
        await GoToSummaryAsync(unticked);
        Assert.Contains("PipelineSetupRecapTelemetryOff", unticked.Find("[data-testid='setup-recap-telemetry']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUntickedTile_TravelsInTheSummaryAddress_AndATickedOneAddsNothing()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var ticked = await OpenCapabilitiesAsync();
        await GoToSummaryAsync(ticked);
        Assert.DoesNotContain(PipelineSetupTelemetry.SkipOption, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);

        var unticked = await OpenCapabilitiesAsync();
        await ClickAsync(unticked, Tile);
        await GoToSummaryAsync(unticked);
        Assert.Contains(PipelineSetupTelemetry.SkipOption, Uri.UnescapeDataString(nav.Uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASummaryAddressWithoutTheOption_ReopensTicked_AndConfiguresTheTelemetry()
    {
        var cut = OpenSummaryFrom("backend,database,ci,candidate,production");

        await FinishAsync(cut, 3);

        cut.WaitForAssertion(() => Assert.Equal(1, ConfigureCalls()));
    }

    [Fact]
    public async Task TheSummaryAddress_WithTheOptionUnticked_ReopensUnticked()
    {
        var cut = OpenSummaryFrom(
            $"backend,database,ci,candidate,production,{PipelineSetupTelemetry.SkipOption},{PipelineSetupAudience.SkipOption}");

        await FinishAsync(cut, 3);

        cut.WaitForAssertion(() => Assert.EndsWith("/projects/7", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
        Assert.Equal(0, ConfigureCalls());
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
