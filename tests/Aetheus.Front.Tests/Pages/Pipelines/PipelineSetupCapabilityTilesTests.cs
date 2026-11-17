// SPDX-License-Identifier: EUPL-1.2
using System.Xml.Linq;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.Architecture;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recettes R-22 and R-23 (2026-11-10): every tile of the Capabilities step has a short title and description and a "?" that
/// opens a dialog with the longer explanation; a tile whose capability needs another one is greyed, with the mention "depends on
/// ...", while that one is unticked, and nothing is ever ticked on the user's behalf. What these tests do not prove is the layout
/// (a tile that overflows, the alignment of the box and the icon, the columns that wrap): bUnit has no layout, so R-24 and R-25
/// are proved by measuring the real page, not here.</summary>
public sealed class PipelineSetupCapabilityTilesTests : BunitContext
{
    private const string DependencyNote = "[data-testid='setup-dependency-note']";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupCapabilityTilesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddScoped<OmniDialogService>(services => new RecordingDialogService(
            services.GetRequiredService<AppDialogs>(), services.GetRequiredService<OmniOverlayService>()));
    }

    private sealed class RecordingDialogService(AppDialogs dialogs, OmniOverlayService overlay) : OmniDialogService(dialogs, overlay)
    {
        public List<(string Title, string Message, string? OkText)> Alerts { get; } = [];

        public override Task Alert(string message, string title, OmniAlertOptions? options = null)
        {
            Alerts.Add((title, message, options?.OkButtonText));
            return Task.CompletedTask;
        }
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync(string architecture = "application", bool? sameProject = false)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, architecture, sameProject: architecture == "application" ? sameProject : null);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        return cut;
    }

    private static Task ClickAsync(IRenderedComponent<PipelineSetupWizard> cut, string key) =>
        cut.Find($"[data-testid='setup-option-{key}']").ClickAsync(new MouseEventArgs());

    private static AngleSharp.Dom.IElement Tile(IRenderedComponent<PipelineSetupWizard> cut, string key) =>
        cut.Find($"[data-testid='setup-option-{key}']");

    private static bool IsChecked(IRenderedComponent<PipelineSetupWizard> cut, string key) =>
        Tile(cut, key).GetAttribute("aria-checked") == "true";

    private static bool IsDisabled(IRenderedComponent<PipelineSetupWizard> cut, string key) =>
        Tile(cut, key).GetAttribute("aria-disabled") == "true";

    private static string? DependsOn(IRenderedComponent<PipelineSetupWizard> cut, string key) =>
        cut.FindAll($"[data-testid='setup-option-{key}'] {DependencyNote}").SingleOrDefault()?.GetAttribute("data-depends-on");

    // R-23: the extended QA and the secret history are started by the nightly only (their templates say so). The nightly is ticked by
    // default, so the two are usable at first; unticking it greys them.
    [Theory]
    [InlineData("qa-extended")]
    [InlineData("security-history")]
    public async Task ANightlyExtension_IsGreyedWithItsMention_WhileTheNightlyIsUnticked(string key)
    {
        var cut = await OpenCapabilitiesAsync();
        Assert.True(IsChecked(cut, "nightly"));
        Assert.False(IsDisabled(cut, key));
        Assert.Null(DependsOn(cut, key));

        await ClickAsync(cut, "nightly");

        Assert.False(IsChecked(cut, "nightly"));
        Assert.True(IsDisabled(cut, key));
        Assert.False(IsChecked(cut, key));
        Assert.Equal("nightly", DependsOn(cut, key));
        Assert.Contains("PipelineSetupDependsOn", cut.Find($"[data-testid='setup-option-{key}'] {DependencyNote}").TextContent, StringComparison.Ordinal);

        // A click on the greyed tile changes nothing: nothing is ticked behind the user's back either way.
        await ClickAsync(cut, key);
        Assert.False(IsChecked(cut, key));
        Assert.False(IsChecked(cut, "nightly"));

        await ClickAsync(cut, "nightly");
        Assert.False(IsDisabled(cut, key));
        Assert.Null(DependsOn(cut, key));
        Assert.False(IsChecked(cut, key));
        await ClickAsync(cut, key);
        Assert.True(IsChecked(cut, key));
    }

    [Fact]
    public async Task UntickingTheNightly_UntickAndGreysItsExtensions_AndTickingItAgainTicksNothing()
    {
        var cut = await OpenCapabilitiesAsync();
        await ClickAsync(cut, "qa-extended");
        await ClickAsync(cut, "security-history");
        Assert.True(IsChecked(cut, "qa-extended"));
        Assert.True(IsChecked(cut, "security-history"));

        await ClickAsync(cut, "nightly");

        foreach (var key in new[] { "qa-extended", "security-history" })
        {
            Assert.False(IsChecked(cut, key));
            Assert.True(IsDisabled(cut, key));
            Assert.Equal("nightly", DependsOn(cut, key));
        }

        await ClickAsync(cut, "nightly");
        foreach (var key in new[] { "qa-extended", "security-history" })
        {
            Assert.False(IsChecked(cut, key));
            Assert.False(IsDisabled(cut, key));
        }
    }

    // R-23: the approval is written into the production deployment, and the candidate starts it.
    [Fact]
    public async Task TheApproval_IsGreyedWhileTheProductionDeploymentIsUnticked_AndIsBackWithItsValueOnce_ItIsTicked()
    {
        var cut = await OpenCapabilitiesAsync();
        Assert.True(IsChecked(cut, "production"));
        Assert.False(IsDisabled(cut, "deploy-approval"));
        Assert.True(IsChecked(cut, "deploy-approval"));
        Assert.Null(DependsOn(cut, "deploy-approval"));

        await ClickAsync(cut, "production");

        Assert.True(IsDisabled(cut, "deploy-approval"));
        Assert.False(IsChecked(cut, "deploy-approval"));
        Assert.Equal("production", DependsOn(cut, "deploy-approval"));
        await ClickAsync(cut, "deploy-approval");
        Assert.False(IsChecked(cut, "deploy-approval"));

        await ClickAsync(cut, "production");
        Assert.False(IsDisabled(cut, "deploy-approval"));
        Assert.Null(DependsOn(cut, "deploy-approval"));
    }

    [Fact]
    public async Task TheDeployAfterCandidate_NamesWhatItWaitsFor_TheCandidateOrTheProductionDeployment()
    {
        var cut = await OpenCapabilitiesAsync();
        Assert.True(IsChecked(cut, "candidate"));
        Assert.False(IsDisabled(cut, "deploy-after-candidate"));

        // The wizard also creates a candidate for the nightly, so with the nightly ticked the candidate tile is not what it waits for.
        await ClickAsync(cut, "candidate");
        Assert.False(IsDisabled(cut, "deploy-after-candidate"));
        await ClickAsync(cut, "nightly");

        Assert.True(IsDisabled(cut, "deploy-after-candidate"));
        Assert.False(IsChecked(cut, "deploy-after-candidate"));
        Assert.Equal("candidate", DependsOn(cut, "deploy-after-candidate"));

        await ClickAsync(cut, "production");
        Assert.Equal("candidate production", DependsOn(cut, "deploy-after-candidate"));

        await ClickAsync(cut, "candidate");
        Assert.Equal("production", DependsOn(cut, "deploy-after-candidate"));

        await ClickAsync(cut, "production");
        Assert.False(IsDisabled(cut, "deploy-after-candidate"));
        Assert.Null(DependsOn(cut, "deploy-after-candidate"));
    }

    [Fact]
    public void TheDependencies_AreTheOnesTheTemplatesDeclare()
    {
        Assert.Equal(["nightly"], PipelineSetupCapabilityDependencies.Of("qa-extended"));
        Assert.Equal(["nightly"], PipelineSetupCapabilityDependencies.Of("security-history"));
        Assert.Equal(["candidate", "production"], PipelineSetupCapabilityDependencies.Of("deploy-after-candidate"));
        Assert.Equal(["production"], PipelineSetupCapabilityDependencies.Of("deploy-approval"));
        Assert.Empty(PipelineSetupCapabilityDependencies.Of("ci"));
        Assert.Equal(["qa-extended", "security-history"], PipelineSetupCapabilityDependencies.Dependents("nightly"));
        Assert.Equal(["deploy-after-candidate", "deploy-approval"], PipelineSetupCapabilityDependencies.Dependents("production"));
    }

    // R-22: a "?" on every tile, which opens the longer explanation in a dialog.
    [Fact]
    public async Task EveryTile_HasAQuestionMark_ThatOpensItsExplanationInADialog_WithoutTogglingTheTile()
    {
        var cut = await OpenCapabilitiesAsync();
        var dialogs = (RecordingDialogService)Services.GetRequiredService<OmniDialogService>();

        var cards = cut.FindAll(".pipeline-setup-tile > .omni-selectable-card");
        Assert.NotEmpty(cards);
        Assert.Equal(cards.Count, cut.FindAll(".pipeline-setup-tile > .pipeline-setup-tile__help").Count);
        // The "?" is the card's sibling, never inside the card (a card is one button and holds nothing interactive).
        Assert.Empty(cut.FindAll(".omni-selectable-card .pipeline-setup-tile__help, .omni-selectable-card button"));

        var before = IsChecked(cut, "ci");
        await cut.Find("[data-testid='setup-help-ci']").ClickAsync(new MouseEventArgs());

        var alert = Assert.Single(dialogs.Alerts);
        Assert.Equal("CI", alert.Title);
        Assert.Equal("CiPipelineHelp", alert.Message);
        Assert.Equal("Close", alert.OkText);
        Assert.Equal(before, IsChecked(cut, "ci"));
    }

    [Fact]
    public async Task TheStrategyTiles_AndTheLanguageTiles_HaveTheirOwnQuestionMark()
    {
        var cut = await OpenCapabilitiesAsync();
        var dialogs = (RecordingDialogService)Services.GetRequiredService<OmniDialogService>();

        await cut.Find("[data-testid='setup-help-strategy-bluegreen']").ClickAsync(new MouseEventArgs());
        await cut.Find("[data-testid='setup-help-deploy-approval']").ClickAsync(new MouseEventArgs());

        Assert.Equal(
            [("PipelineSetupProdBlueGreen", "PipelineSetupStrategyBlueGreenHelp"),
             ("PipelineSetupAskApprovalBeforeProduction", "PipelineSetupAskApprovalBeforeProductionHelp")],
            dialogs.Alerts.Select(alert => (alert.Title, alert.Message)));
        // The help of a tile does not choose it.
        Assert.True(IsChecked(cut, "strategy-replace"));
    }

    // R-22: no title or description may outgrow the fixed tile height; the long text lives behind the "?".
    private static readonly string[] TileTitleKeys =
    [
        "Quality", "Security", "QaExtended", "SecurityHistory", "Candidate", "Nightly", "ProductionDeployment", "FastDeploy", "Rollback",
        "Packages", "PipelineSetupRunDeployAfterCandidate", "PipelineSetupAskApprovalBeforeProduction", "PipelineSetupExpandContract",
        "PipelineSetupDependencyUpdates", "PipelineSetupTelemetry", "PipelineSetupAudience", "ExternalSecurityPipeline",
        "ExternalSecurityCompletePipeline", "ExternalQualityMinimalPipeline", "ExternalQualityPipeline", "PipelineSetupProdReplace",
        "PipelineSetupProdBlueGreen"
    ];

    private static readonly string[] TileHintKeys =
    [
        "CiPipelineHint", "QualityPipelineHint", "SecurityPipelineHint", "QaPipelineHint", "QaExtendedPipelineHint",
        "SecurityHistoryPipelineHint", "CandidatePipelineHint", "NightlyPipelineHint", "ProductionPipelineHint", "FastPipelineHint",
        "RollbackPipelineHint", "PackagesPipelineHint", "PipelineSetupRunDeployAfterCandidateHint",
        "PipelineSetupAskApprovalBeforeProductionHint", "PipelineSetupExpandContractHint", "PipelineSetupDependencyUpdatesHint",
        "PipelineSetupTelemetryHint", "PipelineSetupAudienceHint", "ExternalSecurityPipelineHint", "ExternalSecurityCompletePipelineHint",
        "ExternalQualityMinimalPipelineHint", "ExternalQualityPipelineHint", "PipelineLanguageDotNetHint", "PipelineLanguagePythonHint",
        "PipelineLanguageJavaHint", "PipelineSetupStrategyReplaceHint", "PipelineSetupStrategyBlueGreenHint"
    ];

    private static IReadOnlyDictionary<string, string> Resources(string fileName) =>
        XDocument.Load(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", fileName)).Root!
            .Elements("data")
            .ToDictionary(item => (string)item.Attribute("name")!, item => item.Element("value")!.Value, StringComparer.Ordinal);

    [Theory]
    [InlineData("AppStrings.resx")]
    [InlineData("AppStrings.fr-FR.resx")]
    public void TheTileTexts_AreShort_AndEveryDescriptionHasItsLongerExplanation(string file)
    {
        var resources = Resources(file);

        var longTitles = TileTitleKeys.Where(key => resources[key].Length > 30).Select(key => $"{key}: {resources[key].Length}").ToList();
        Assert.True(longTitles.Count == 0, "Tile titles over 30 characters wrap on more than two lines:" + Environment.NewLine + string.Join(Environment.NewLine, longTitles));

        var longHints = TileHintKeys.Where(key => resources[key].Length > 80).Select(key => $"{key}: {resources[key].Length}").ToList();
        Assert.True(longHints.Count == 0, "Tile descriptions over 80 characters outgrow the tile:" + Environment.NewLine + string.Join(Environment.NewLine, longHints));

        var withoutHelp = TileHintKeys
            .Select(key => key[..^"Hint".Length] + "Help")
            .Where(key => !resources.TryGetValue(key, out var help) || help.Length <= resources[key[..^"Help".Length] + "Hint"].Length)
            .ToList();
        Assert.True(withoutHelp.Count == 0, "Tile help missing or not longer than the description:" + Environment.NewLine + string.Join(Environment.NewLine, withoutHelp));
    }
}
