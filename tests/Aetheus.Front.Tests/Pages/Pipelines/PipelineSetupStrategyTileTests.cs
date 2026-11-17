// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recettes R-15 and R-19 (mockup validated by the user on 2026-11-09): the Capabilities step stacks its categories in one
/// order (Construction, Automatisation, Analyse, Livraison, Supervision), every tile of a category sharing one OE grid, and the
/// production strategy is (R-26, user decision 2026-11-10) two exclusive tiles under the grid of Livraison, the replacement of the
/// services by default, blue-green no longer labelled as recommended.</summary>
public sealed class PipelineSetupStrategyTileTests : BunitContext
{
    private const string StrategyGroup = "[data-testid='setup-strategy']";
    private const string ReplaceTile = "[data-testid='setup-option-strategy-replace']";
    private const string BlueGreenTile = "[data-testid='setup-option-strategy-bluegreen']";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupStrategyTileTests() => _handler = BunitTestHelper.RegisterServices(this);

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync(
        string architecture = "application", bool? sameProject = false)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
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

    /// <summary>Picks a strategy the way the user does on the Capabilities step (a click on its tile); shared with the summary tests.</summary>
    internal static Task ChooseStrategyAsync(IRenderedComponent<PipelineSetupWizard> cut, string strategy) =>
        cut.Find(strategy == ProductionStrategy.BlueGreen ? BlueGreenTile : ReplaceTile).ClickAsync(new MouseEventArgs());

    private static string[] CheckedStrategies(IRenderedComponent<PipelineSetupWizard> cut) =>
        [.. cut.FindAll($"{StrategyGroup} [aria-checked='true']").Select(item => item.GetAttribute("data-testid")!)];

    private static Task ClickAsync(IRenderedComponent<PipelineSetupWizard> cut, string testId) =>
        cut.Find($"[data-testid='{testId}']").ClickAsync(new MouseEventArgs());

    private static string[] CategoryKeys(IRenderedComponent<PipelineSetupWizard> cut) =>
        [.. cut.FindAll("[data-testid^='setup-category-']").Select(item => item.GetAttribute("data-testid")!["setup-category-".Length..])];

    [Fact]
    public async Task TheCategories_AreStackedInTheOrderConstructionAutomationAnalysisDeliverySupervision()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.Equal(["build", "automation", "analysis", "delivery", "supervision"], CategoryKeys(cut));
        // The titles are the French names of the mockup, in the same order (the stub localizer returns the keys).
        Assert.Equal(
            ["PipelineSetupCategoryBuild", "PipelineSetupCategoryAutomation", "PipelineSetupCategoryAnalysis",
             "PipelineSetupCategoryDelivery", "PipelineSetupSupervision"],
            cut.FindAll("[data-testid^='setup-category-'] > h6:first-of-type").Select(item => item.TextContent.Trim()));
    }

    [Fact]
    public async Task EveryTile_LivesInACategory_AndTheCategoriesHoldTheMockupsTiles()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.Equal(["ci", "packages"], TilesOf(cut, "build"));
        Assert.Equal(["nightly", "dependency-updates"], TilesOf(cut, "automation"));
        Assert.Equal(["quality", "security", "qa", "qa-extended", "security-history"], TilesOf(cut, "analysis"));
        Assert.Equal(["candidate", "deploy-after-candidate", "deploy-approval", "production", "fast-deploy", "rollback", "expand-contract"], TilesOf(cut, "delivery"));
        Assert.Equal(["telemetry", "audience"], TilesOf(cut, "supervision"));
        // No tile sits outside a category.
        Assert.Equal(
            cut.FindAll("[data-testid^='setup-option-']").Count(item => !item.GetAttribute("data-testid")!.StartsWith("setup-option-lang-", StringComparison.Ordinal)),
            cut.FindAll("[data-testid^='setup-category-'] [data-testid^='setup-option-']").Count(item => !item.GetAttribute("data-testid")!.StartsWith("setup-option-lang-", StringComparison.Ordinal)));
    }

    private static string[] TilesOf(IRenderedComponent<PipelineSetupWizard> cut, string category) =>
        [.. cut.FindAll($"[data-testid='setup-category-{category}'] [data-testid^='setup-option-']")
            .Select(item => item.GetAttribute("data-testid")!["setup-option-".Length..])
            .Where(key => !key.StartsWith("lang-", StringComparison.Ordinal) && !key.StartsWith("strategy-", StringComparison.Ordinal))];

    [Fact]
    public void TheCategoryList_PutsEveryCapabilityAnArchitectureProposesInACategory_InTheOrderOfTheMockup()
    {
        Assert.Equal(["build", "automation", "analysis", "delivery", "supervision"], PipelineSetupCategories.All.Select(item => item.Key));
        var categorised = PipelineSetupCategories.All.SelectMany(item => item.Capabilities).ToList();
        Assert.Equal(categorised.Count, categorised.Distinct().Count());
        foreach (var architecture in Enum.GetValues<PipelineArchitecture>())
        {
            foreach (var capability in categorised.Where(key => PipelineSetupArchitecture.Proposes(architecture, key)))
                Assert.Contains(capability, categorised);
        }
        string[] applicationCapabilities =
        [
            "ci", "quality", "security", "qa", "qa-extended", "security-history", "candidate", "nightly",
            "production", "fast-deploy", "rollback", "packages", "telemetry"
        ];
        Assert.All(applicationCapabilities, key => Assert.Contains(key, categorised));
        Assert.Contains(PipelineSetupCategories.ProductionStrategy, PipelineSetupCategories.All.Single(item => item.Key == "delivery").Capabilities);
    }

    [Fact]
    public async Task TheStrategy_IsTwoSelectableTilesOfAnOeRadioGroup_UnderTheGridOfTheDeliveryCategory()
    {
        var cut = await OpenCapabilitiesAsync();

        var group = Assert.Single(cut.FindAll($"[data-testid='setup-category-delivery'] {StrategyGroup}"));
        Assert.Single(cut.FindAll(StrategyGroup));
        Assert.Equal("radiogroup", group.GetAttribute("role"));
        Assert.Equal("PipelineSetupProdStrategyTile", group.GetAttribute("aria-label"));
        // Two OE selectable cards, one tile each (its own "?" included), no select bar and no double tile any more.
        Assert.Equal(2, cut.FindAll($"{StrategyGroup} .omni-selectable-card").Count);
        Assert.Equal(["radio", "radio"], cut.FindAll($"{StrategyGroup} .omni-selectable-card").Select(item => item.GetAttribute("role")));
        Assert.Equal(2, cut.FindAll($"{StrategyGroup} .pipeline-setup-tile__help").Count);
        Assert.Empty(cut.FindComponents<OmniSelectBar<string>>());
        Assert.DoesNotContain(cut.FindComponents<OmniSelectableCard>(), item => item.Instance.DoubleWidth);
        Assert.Empty(cut.FindAll("[data-testid='setup-strategy-choice']"));
    }

    [Fact]
    public async Task TheStrategy_IsTheReplacementByDefault_AndBlueGreenIsNotLabelledRecommended()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.Equal(["setup-option-strategy-replace"], CheckedStrategies(cut));
        Assert.Contains("PipelineSetupProdReplace", cut.Find(ReplaceTile).TextContent, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupStrategyReplaceHint", cut.Find(ReplaceTile).TextContent, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupProdBlueGreen", cut.Find(BlueGreenTile).TextContent, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupStrategyBlueGreenHint", cut.Find(BlueGreenTile).TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("ecommand", cut.Find(StrategyGroup).TextContent, StringComparison.OrdinalIgnoreCase);
        // A project that is not a monolith has nothing to explain under the tiles.
        Assert.Empty(cut.FindAll("[data-testid='setup-strategy-hint']"));
    }

    [Fact]
    public async Task TheChoice_IsExclusive_PickingOneUnpicksTheOther()
    {
        var cut = await OpenCapabilitiesAsync();

        await ChooseStrategyAsync(cut, ProductionStrategy.BlueGreen);
        Assert.Equal(["setup-option-strategy-bluegreen"], CheckedStrategies(cut));
        Assert.Equal("false", cut.Find(ReplaceTile).GetAttribute("aria-checked"));

        await ChooseStrategyAsync(cut, ProductionStrategy.Replace);
        Assert.Equal(["setup-option-strategy-replace"], CheckedStrategies(cut));
        Assert.Equal("false", cut.Find(BlueGreenTile).GetAttribute("aria-checked"));

        // Picking the chosen tile again keeps it chosen: a radio never unpicks itself.
        await ChooseStrategyAsync(cut, ProductionStrategy.Replace);
        Assert.Equal(["setup-option-strategy-replace"], CheckedStrategies(cut));
    }

    [Fact]
    public async Task TheStrategyTile_IsOffered_OnlyWhileAProductionDeploymentIsSelected()
    {
        var cut = await OpenCapabilitiesAsync();
        Assert.Single(cut.FindAll(StrategyGroup));

        await ClickAsync(cut, "setup-option-production");
        Assert.Empty(cut.FindAll(StrategyGroup));

        await ClickAsync(cut, "setup-option-production");
        Assert.Single(cut.FindAll(StrategyGroup));
    }

    [Fact]
    public async Task AMonolith_ShowsTheReplacementAsTheOnlyChoice()
    {
        var cut = await OpenCapabilitiesAsync(sameProject: true);

        Assert.Equal(["setup-option-strategy-replace"], CheckedStrategies(cut));
        Assert.Equal(["true", "true"], cut.FindAll($"{StrategyGroup} .omni-selectable-card").Select(item => item.GetAttribute("aria-disabled")));
        Assert.Equal("PipelineSetupProdMonolithStrategyHelp", cut.Find("[data-testid='setup-strategy-hint']").TextContent.Trim());

        // The disabled blue-green tile ignores a click: the replacement stays chosen.
        await ChooseStrategyAsync(cut, ProductionStrategy.BlueGreen);
        Assert.Equal(["setup-option-strategy-replace"], CheckedStrategies(cut));
    }

    [Fact]
    public async Task AnExternalRepository_AndAPackage_HaveNoStrategyTile()
    {
        foreach (var architecture in new[] { "external", "package" })
        {
            var cut = await OpenCapabilitiesAsync(architecture);
            Assert.Empty(cut.FindAll(StrategyGroup));
            Assert.DoesNotContain("setup-category-delivery", CategoryKeys(cut).Select(key => $"setup-category-{key}"));
            Assert.DoesNotContain("supervision", CategoryKeys(cut));
        }
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
