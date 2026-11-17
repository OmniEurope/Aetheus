// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-26 (user decision 2026-11-05): the wizard's Supervision section offers the telemetry and the audience of an
/// application. The audience starts ticked when the project has a web front, and the choice (with the telemetry) is what the
/// server is asked to prepare once the pipelines exist. The preparation never blocks the wizard, and what the server did is kept
/// for the project's overview, a failed item staying failed.</summary>
public sealed class PipelineSetupAudienceTests : BunitContext
{
    private const string Supervision = "[data-testid='setup-category-supervision']";
    private const string AudienceTile = "setup-option-audience";
    private const string CollectionUrl = "api/pipelines/setup/collection";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupAudienceTests() => _handler = BunitTestHelper.RegisterServices(this);

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync(
        string architecture = "application", bool? sameProject = false, bool untickFrontend = false)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, architecture, sameProject: architecture == "application" ? sameProject : null);
        if (untickFrontend)
            await cut.Find("[data-testid='setup-option-frontend']").ClickAsync(new MouseEventArgs());
        await cut.InvokeAsync(wizard.Instance.NextStep);
        return cut;
    }

    private static string Checked(IRenderedComponent<PipelineSetupWizard> cut, string tile) =>
        cut.Find($"[data-testid='{tile}']").GetAttribute("aria-checked")!;

    private async Task FinishAsync(IRenderedComponent<PipelineSetupWizard> cut)
    {
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.EndsWith("/projects/7", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
    }

    private string? CollectionBody() => _handler.RequestDetails
        .SingleOrDefault(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith(CollectionUrl, StringComparison.OrdinalIgnoreCase)).Body;

    [Fact]
    public async Task TheSupervisionSection_OffersTheTelemetryAndTheAudience_ForAnApplication()
    {
        var cut = await OpenCapabilitiesAsync();

        var section = cut.Find(Supervision);
        Assert.NotNull(section.QuerySelector("[data-testid='setup-option-telemetry']"));
        Assert.NotNull(section.QuerySelector($"[data-testid='{AudienceTile}']"));
    }

    [Theory]
    [InlineData("package")]
    [InlineData("external")]
    public async Task TheSupervisionSection_IsNotOffered_ForAPackageOrAnExternalRepository(string architecture)
    {
        var cut = await OpenCapabilitiesAsync(architecture);

        Assert.Empty(cut.FindAll(Supervision));
    }

    [Theory]
    [InlineData(true, false, "true")]
    [InlineData(false, false, "true")]
    [InlineData(false, true, "false")]
    public async Task TheAudience_StartsTickedWhenTheProjectHasAWebFront(bool fullStack, bool untickFrontend, string expected)
    {
        var cut = await OpenCapabilitiesAsync(sameProject: fullStack, untickFrontend: untickFrontend);

        Assert.Equal(expected, Checked(cut, AudienceTile));
    }

    [Fact]
    public async Task WhenTicked_FinishAsksTheServerToPrepareBothSignals_OnceTheTilesAreChosen()
    {
        _handler.SetJsonResponse(HttpMethod.Post, CollectionUrl, new PipelineSetupCollectionResultDto());
        var cut = await OpenCapabilitiesAsync();

        await FinishAsync(cut);

        var body = CollectionBody();
        Assert.Contains("\"projectId\":7", body, StringComparison.Ordinal);
        Assert.Contains("\"telemetry\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"audience\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUntickedAudience_IsNotAsked_AndTravelsInTheSummaryAddress()
    {
        _handler.SetJsonResponse(HttpMethod.Post, CollectionUrl, new PipelineSetupCollectionResultDto());
        var cut = await OpenCapabilitiesAsync();
        await cut.Find($"[data-testid='{AudienceTile}']").ClickAsync(new MouseEventArgs());
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(PipelineSetupAudience.SkipOption,
            Uri.UnescapeDataString(Services.GetRequiredService<NavigationManager>().Uri), StringComparison.Ordinal);
        Assert.Contains("PipelineSetupRecapAudienceOff", cut.Find("[data-testid='setup-recap-audience']").TextContent, StringComparison.Ordinal);

        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.NotNull(CollectionBody()));

        Assert.Contains("\"audience\":false", CollectionBody(), StringComparison.Ordinal);
        Assert.Contains("\"telemetry\":true", CollectionBody(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummary_SaysWhatIsPrepared_WhenTheAudienceIsTicked()
    {
        var cut = await OpenCapabilitiesAsync();
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));

        Assert.Contains("PipelineSetupRecapAudienceOn", cut.Find("[data-testid='setup-recap-audience']").TextContent, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupRecapSupervisionPreparedAudience",
            cut.Find("[data-testid='setup-recap-supervision-prepared']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResultOfThePreparation_IsKeptItemByItem_ForTheOverview_AFailureStayingAFailure()
    {
        _handler.SetJsonResponse(HttpMethod.Post, CollectionUrl, new PipelineSetupCollectionResultDto
        {
            Items =
            [
                new() { Kind = PipelineSetupCollectionKind.AppEnabled, Outcome = PipelineSetupCollectionOutcome.Done },
                new() { Kind = PipelineSetupCollectionKind.Origins, Outcome = PipelineSetupCollectionOutcome.Failed, Detail = "No production address is known yet" }
            ]
        });
        var cut = await OpenCapabilitiesAsync();

        await FinishAsync(cut);

        var recorded = Services.GetRequiredService<PipelineSetupCollectionState>().For(7);
        Assert.NotNull(recorded);
        Assert.Equal(2, recorded.Items.Count);
        Assert.True(recorded.HasFailure);
    }

    [Fact]
    public async Task ARefusedPreparation_NeverBlocksTheWizard_NothingIsRecorded_AndThePipelinesStayCreated()
    {
        var cut = await OpenCapabilitiesAsync();
        _handler.SetResponse(HttpMethod.Post, CollectionUrl, HttpStatusCode.InternalServerError);

        await FinishAsync(cut);

        Assert.Null(Services.GetRequiredService<PipelineSetupCollectionState>().For(7));
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Method == HttpMethod.Delete.Method);
    }

    [Fact]
    public void TheDefault_FollowsTheShape_UntilTheUserDecides()
    {
        var audience = new PipelineSetupAudience();

        audience.ApplyDefault(PipelineArchitecture.Application, sameProject: false, frontend: false);
        Assert.False(audience.Configure);
        audience.ApplyDefault(PipelineArchitecture.Application, sameProject: true, frontend: false);
        Assert.True(audience.Configure);

        audience.Configure = false;
        audience.Touched = true;
        audience.ApplyDefault(PipelineArchitecture.Application, sameProject: true, frontend: true);
        Assert.False(audience.Configure);
    }

    [Fact]
    public void WhileTheShapeIsNotChosen_TheDefaultStaysTicked()
    {
        var audience = new PipelineSetupAudience();

        audience.ApplyDefault(PipelineArchitecture.Application, sameProject: null, frontend: false);

        Assert.True(audience.Configure);
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back",
            "package-telemetry", "application-security-external"
        }.Select(name => new PipelineTemplateSummaryDto
        {
            Id = name.GetHashCode(StringComparison.Ordinal),
            Name = name,
            Version = 1
        })
    ];
}
