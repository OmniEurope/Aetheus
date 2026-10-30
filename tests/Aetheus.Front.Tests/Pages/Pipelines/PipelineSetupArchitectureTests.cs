// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Xml.Linq;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary> The setup wizard's second step asks the type of what the pipelines build and check,
/// and the type decides the pipelines proposed and the variables written. </summary>
public sealed class PipelineSetupArchitectureTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupArchitectureTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public async Task ArchitectureStep_AsksTheTypeFirst_AndCannotGoOnWithoutOne()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        Assert.Equal(3, cut.FindAll("[data-testid^='setup-architecture-']").Count);
        // No type chosen in advance, so no detail and no way on yet.
        Assert.Empty(cut.FindAll("[data-testid^='setup-option-']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-shape']"));
        Assert.False(wizard.Instance.CanAdvance);
        Assert.Contains("PipelineArchitectureQuestion", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>R2-3: the application shape is an exclusive choice between two OE tiles, without a
    /// default; nothing below it is shown before one is chosen.</summary>
    [Fact]
    public async Task Application_AsksTheShapeWithTwoExclusiveTiles_AndNoDefaultAnswer()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await Choose(cut, "application");

        var tiles = cut.FindAll("[data-testid='setup-shape'] button.omni-selectable-card");
        Assert.Equal(2, tiles.Count);
        Assert.All(tiles, tile => Assert.Equal("false", tile.GetAttribute("aria-checked")));
        Assert.Contains("PipelineShapeFullStack", tiles[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("PipelineShapeSeparate", tiles[1].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='setup-parts']"));
        Assert.False(wizard.Instance.CanAdvance);
    }

    /// <summary>R2-3: choosing a tile unchooses the other one.</summary>
    [Fact]
    public async Task TheTwoShapeTiles_AreExclusive()
    {
        var (cut, _) = await OpenArchitectureStepAsync();
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: true);
        Assert.Equal("true", cut.Find("[data-testid='setup-shape-fullstack']").GetAttribute("aria-checked"));

        await cut.Find("[data-testid='setup-shape-separate']").ClickAsync(new MouseEventArgs());

        Assert.Equal("false", cut.Find("[data-testid='setup-shape-fullstack']").GetAttribute("aria-checked"));
        Assert.Equal("true", cut.Find("[data-testid='setup-shape-separate']").GetAttribute("aria-checked"));
    }

    [Fact]
    public async Task FullStack_ShowsOnlyTheDatabasePart_AndClaimsNoSeparateFrontend()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: true);

        Assert.True(wizard.Instance.CanAdvance);
        var option = Assert.Single(cut.FindAll("[data-testid^='setup-option-']"));
        Assert.Contains("Database", option.TextContent, StringComparison.Ordinal);
        // R2-2: the database is ticked by default.
        Assert.Equal("true", option.GetAttribute("aria-checked"));

        var requests = await FinishAsync(cut, wizard);

        Assert.Equal(
            ["atlas-ci", "atlas-quality", "atlas-security", "atlas-qa", "atlas-candidate", "atlas-nightly", "atlas-deploy-prod"],
            requests.Select(request => request.Name));
        Assert.All(requests, request =>
        {
            Assert.Contains("APPLICATION_HAS_FRONTEND: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_BACKEND: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_DATABASE: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
        });
    }

    /// <summary>R2-3: under the separate tile the front-end and the back-end are ticked one by one (one,
    /// the other or both), then the database; with neither, the step cannot go on.</summary>
    [Fact]
    public async Task Separate_TicksTheFrontendAndTheBackendApart_ThenTheDatabase()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);

        Assert.Equal(
            ["frontend", "backend", "database"],
            cut.FindAll("[data-testid='setup-parts'] [data-testid^='setup-option-']")
                .Select(option => option.GetAttribute("data-testid")!["setup-option-".Length..]));
        Assert.True(wizard.Instance.CanAdvance);

        await Toggle(cut, "frontend");
        Assert.True(wizard.Instance.CanAdvance);
        await Toggle(cut, "backend");
        Assert.False(wizard.Instance.CanAdvance);
        await Toggle(cut, "frontend");
        Assert.True(wizard.Instance.CanAdvance);
    }

    /// <summary>R2-3: a back-end alone, without front-end nor database, is written as such.</summary>
    [Fact]
    public async Task Separate_WithTheBackendAlone_ClaimsNeitherFrontendNorDatabase()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await Toggle(cut, "frontend");
        await Toggle(cut, "database");

        var requests = await FinishAsync(cut, wizard);

        Assert.All(requests, request =>
        {
            Assert.Contains("APPLICATION_HAS_FRONTEND: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_BACKEND: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_DATABASE: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
        });
    }

    /// <summary>R2-4 and R2-5: every option is an OE selectable card (a check box), not a hand-made label.</summary>
    [Fact]
    public async Task Options_AreOeSelectableCards_OfCheckBoxRole()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);

        // The pipelines' options (12), the three languages of the quality pipeline (R-105) and the deployment after the
        // candidate (R-72, offered by default since the candidate and the deployment are both ticked), all of the same kind.
        var options = cut.FindAll("[data-testid^='setup-option-']");

        Assert.Equal(16, options.Count);
        Assert.All(options, option =>
        {
            Assert.Contains("omni-selectable-card", option.ClassName, StringComparison.Ordinal);
            Assert.Equal("checkbox", option.GetAttribute("role"));
        });
        Assert.Empty(cut.FindAll(".pipeline-setup-option"));
    }

    /// <summary>R-4: on the capabilities step the check box, the icon and the title sit on the first line of
    /// each card and the description follows below, over the card's whole width (OE 1.9.1 layout, no option
    /// to set: the page only supplies Title, Description and Icon).</summary>
    [Fact]
    public async Task CapabilityCards_PutBoxIconAndTitleOnTheFirstLine_AndTheDescriptionBelow()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);

        var options = cut.FindAll("[data-testid^='setup-option-']");

        Assert.NotEmpty(options);
        Assert.All(options, option => AssertHeadThenDescription(option, expectBox: true));
    }

    /// <summary>R-4: the architecture cards (radio) follow the same layout, without the box.</summary>
    [Fact]
    public async Task ArchitectureCards_PutIconAndTitleOnTheFirstLine_AndTheDescriptionBelow()
    {
        var (cut, _) = await OpenArchitectureStepAsync();

        var cards = cut.FindAll("[data-testid^='setup-architecture-']");

        Assert.Equal(3, cards.Count);
        Assert.All(cards, card => AssertHeadThenDescription(card, expectBox: false));
    }

    private static void AssertHeadThenDescription(AngleSharp.Dom.IElement card, bool expectBox)
    {
        var parts = card.Children.Select(child => child.ClassName).ToList();
        Assert.Equal("omni-selectable-card__head", parts[0]);
        Assert.Equal("omni-selectable-card__description", parts[1]);

        var head = card.Children[0];
        Assert.Equal(expectBox ? 1 : 0, head.QuerySelectorAll(".omni-selectable-card__box").Length);
        Assert.Single(head.QuerySelectorAll(".omni-selectable-card__icon"));
        Assert.Single(head.QuerySelectorAll(".omni-selectable-card__title"));
        // The description is never inside the head (a narrow column beside the box and the icon).
        Assert.Empty(head.QuerySelectorAll(".omni-selectable-card__description"));
        Assert.False(string.IsNullOrWhiteSpace(card.Children[1].TextContent));
    }

    /// <summary>R2-15: nothing is created that was not chosen. The package pipelines switched on by
    /// the Package type must not stay on when the type goes back to Application.</summary>
    [Fact]
    public async Task SwitchingFromPackageBackToApplication_DoesNotCreateThePackagePipelines()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await Choose(cut, "package");
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        var requests = await FinishAsync(cut, wizard);

        Assert.Equal(
            ["atlas-ci", "atlas-quality", "atlas-security", "atlas-qa", "atlas-candidate", "atlas-nightly", "atlas-deploy-prod"],
            requests.Select(request => request.Name));
    }

    /// <summary>R2-15: the options chosen under one type are not remembered under another, so the
    /// application shape answered before is asked again after a detour through another type.</summary>
    [Fact]
    public async Task ChangingTheType_ForgetsTheApplicationShapeAndTheOptionsOfTheOtherType()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: true);
        Assert.True(wizard.Instance.CanAdvance);

        await Choose(cut, "external");
        await Choose(cut, "application");

        Assert.False(wizard.Instance.CanAdvance);
    }

    [Fact]
    public async Task Package_ProposesTheRepositoryPackageSet_AndNoDeliveryPipeline()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await Choose(cut, "package");
        await cut.InvokeAsync(wizard.Instance.NextStep);

        Assert.Equal(["CI", "Quality", "Security", "Packages"], OptionTitles(cut));

        var requests = await FinishFromCapabilitiesAsync(cut, wizard);

        Assert.Equal(
            ["atlas-ci", "atlas-quality", "atlas-security", "atlas-package-telemetry",
             "atlas-package-web-analytics-dotnet", "atlas-package-web-analytics-browser",
             "atlas-publish-observability-packages"],
            requests.Select(request => request.Name));
        Assert.All(requests, request =>
        {
            Assert.Contains("APPLICATION_HAS_FRONTEND: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_BACKEND: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_ROLLBACK_ENABLED: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_QA_ENABLED: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
        });
    }

    /// <summary>An external repository gets the one pipeline that needs no file in it.</summary>
    [Fact]
    public async Task External_ProposesAndCreatesOnlyTheMinimalSecurityPipeline()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await Choose(cut, "external");
        Assert.True(wizard.Instance.CanAdvance);
        Assert.Empty(cut.FindAll("[data-testid='setup-shape']"));
        await cut.InvokeAsync(wizard.Instance.NextStep);

        Assert.Equal(["ExternalSecurityPipeline"], OptionTitles(cut));
        Assert.True(wizard.Instance.CanAdvance);

        var requests = await FinishFromCapabilitiesAsync(cut, wizard);

        var request = Assert.Single(requests);
        Assert.Equal("atlas-security", request.Name);
        Assert.Contains("extends: application-security-external@1", request.YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_HAS_FRONTEND: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_HAS_BACKEND: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_WithItsOnlyPipelineUnchecked_CannotGoOn()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();
        await Choose(cut, "external");
        await cut.InvokeAsync(wizard.Instance.NextStep);

        await Toggle(cut, "external-security");

        Assert.False(wizard.Instance.CanAdvance);
    }

    [Fact]
    public async Task ChoicesHiddenByTheType_CreateNothing()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();
        // Nightly chosen under an application, then the type becomes a package.
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await Toggle(cut, "nightly");
        await cut.Find("button.omni-wizard__previous").ClickAsync(new MouseEventArgs());
        await Choose(cut, "package");
        await cut.InvokeAsync(wizard.Instance.NextStep);

        var requests = await FinishFromCapabilitiesAsync(cut, wizard);

        Assert.DoesNotContain(requests, request => request.Name is "atlas-nightly" or "atlas-candidate" or "atlas-deploy-prod" or "atlas-qa");
        Assert.DoesNotContain(requests, request =>
            request.YamlDefinition.Contains("application-security-external", StringComparison.Ordinal));
    }

    [Fact]
    public void SummaryAddress_WithATypeAndAnAnswer_ReopensThem()
    {
        OpenSummaryAddress("options=database%2Cci%2Cnightly&architecture=application&sameProject=yes");

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindComponent<WizardDialog>().Instance.CurrentStep));
        Assert.Contains("PipelineArchitectureApplication · PipelineShapeFullStack · Database", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("atlas-nightly", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>The summary names the shape as the step 2 tiles do: Full-stack, or the front-end and
    /// back-end apart (never the former "interface served by the server" wording).</summary>
    [Theory]
    [InlineData("yes", "PipelineArchitectureApplication · PipelineShapeFullStack · Database")]
    [InlineData("no", "PipelineArchitectureApplication · PipelineShapeSeparate · Frontend · Backend · Database")]
    public void Summary_NamesTheShapeLikeTheStepTwoTiles(string sameProject, string expected)
    {
        OpenSummaryAddress($"options=frontend%2Cbackend%2Cdatabase%2Cci&architecture=application&sameProject={sameProject}");

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindComponent<WizardDialog>().Instance.CurrentStep));
        Assert.Contains(expected, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PipelineSameProjectSummary", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryAddress_OfAnApplicationNotYetAnswered_DoesNotReopenTheSummary()
    {
        OpenSummaryAddress("options=ci&architecture=application");

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(0, cut.FindComponent<WizardDialog>().Instance.CurrentStep);
    }

    [Fact]
    public void Rules_SameProjectIsOneServerProject_PackageAndExternalHaveNoApplicationParts()
    {
        Assert.Equal((false, true, false), PipelineSetupArchitecture.Parts(PipelineArchitecture.Application, true, true, true, false));
        Assert.Equal((true, false, true), PipelineSetupArchitecture.Parts(PipelineArchitecture.Application, false, true, false, true));
        Assert.Equal((false, false, false), PipelineSetupArchitecture.Parts(PipelineArchitecture.Application, null, true, true, true));
        Assert.Equal((false, false, false), PipelineSetupArchitecture.Parts(PipelineArchitecture.Package, true, true, true, true));
        Assert.Equal((false, false, false), PipelineSetupArchitecture.Parts(PipelineArchitecture.External, true, true, true, true));
        Assert.False(PipelineSetupArchitecture.IsComplete(PipelineArchitecture.Application, null, true, true));
        Assert.False(PipelineSetupArchitecture.IsComplete(PipelineArchitecture.Application, false, false, false));
        Assert.True(PipelineSetupArchitecture.IsComplete(PipelineArchitecture.Application, true, false, false));
        Assert.False(PipelineSetupArchitecture.IsComplete(null, true, true, true));
        Assert.False(PipelineSetupArchitecture.Proposes(PipelineArchitecture.External, "security"));
        Assert.True(PipelineSetupArchitecture.Proposes(PipelineArchitecture.External, "external-security"));
        Assert.False(PipelineSetupArchitecture.Proposes(PipelineArchitecture.Application, "external-security"));
        Assert.False(PipelineSetupArchitecture.Proposes(PipelineArchitecture.Package, "production"));
        Assert.True(PipelineSetupArchitecture.Proposes(PipelineArchitecture.Application, "rollback"));
        foreach (var architecture in Enum.GetValues<PipelineArchitecture>())
            Assert.Equal(architecture, PipelineSetupArchitecture.FromSlug(PipelineSetupArchitecture.Slug(architecture)));
    }

    /// <summary>The technology is a list that only holds what is implemented:.NET alone
    /// today.</summary>
    [Fact]
    public void Stacks_OfferOnlyWhatIsImplemented()
    {
        var stack = Assert.Single(PipelineSetupStack.All);
        Assert.Equal("package-web-analytics-dotnet", stack.AnalyticsPackageTemplate);
        Assert.Same(PipelineSetupStack.DotNet, PipelineSetupStack.Find("DOTNET"));
        Assert.Null(PipelineSetupStack.Find("node"));
    }

    /// <summary>The step is named after what is built and checked, and the monolith tile is named
    /// Full-stack (R2-3), never a type name nor the names the user refused.</summary>
    [Theory]
    [InlineData("AppStrings.resx", "Pipeline architecture", "Full-stack", "Separate front-end and back-end")]
    [InlineData("AppStrings.fr-FR.resx", "Architecture du pipeline", "Full-stack", "Front-end et Back-end séparés")]
    public void StepNameAndShapeTiles_AreTheChosenOnes(string file, string title, string fullStack, string separate)
    {
        var document = XDocument.Load(Path.Combine(
            Aetheus.Front.Tests.Architecture.RepositoryScan.Root, "src", "Aetheus.Front", "Resources", file));
        var values = document.Root!.Elements("data")
            .ToDictionary(element => (string)element.Attribute("name")!, element => element.Element("value")!.Value);

        Assert.Equal(title, values["PipelineArchitectureTitle"]);
        Assert.Equal(fullStack, values["PipelineShapeFullStack"]);
        Assert.Equal(separate, values["PipelineShapeSeparate"]);
        Assert.DoesNotContain(values.Values, value =>
            value.Contains("tout-en-un", StringComparison.OrdinalIgnoreCase)
            || value.Contains("all-in-one", StringComparison.OrdinalIgnoreCase));
    }

    private void OpenSummaryAddress(string query)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        NothingMissing();
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"http://localhost/pipelines/setup?projectId=7&prefix=atlas&{query}&step=summary");
    }

    private async Task<(IRenderedComponent<PipelineSetupWizard> Cut, IRenderedComponent<WizardDialog> Wizard)> OpenArchitectureStepAsync()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        NothingMissing();
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        return (cut, wizard);
    }

    private static Task Choose(IRenderedComponent<PipelineSetupWizard> cut, string slug) =>
        cut.Find($"[data-testid='setup-architecture-{slug}']").ClickAsync(new MouseEventArgs());

    /// <summary>Clicks an option card, which switches it on or off.</summary>
    private static Task Toggle(IRenderedComponent<PipelineSetupWizard> cut, string key) =>
        cut.Find($"[data-testid='setup-option-{key}']").ClickAsync(new MouseEventArgs());

    private static string[] OptionTitles(IRenderedComponent<PipelineSetupWizard> cut) =>
        cut.FindAll("[data-testid^='setup-option-']:not([data-testid^='setup-option-lang-']) .omni-selectable-card__title")
            .Select(title => title.TextContent).ToArray();

    private async Task<CreatePipelineRequest[]> FinishAsync(
        IRenderedComponent<PipelineSetupWizard> cut, IRenderedComponent<WizardDialog> wizard)
    {
        await cut.InvokeAsync(wizard.Instance.NextStep);
        return await FinishFromCapabilitiesAsync(cut, wizard);
    }

    private async Task<CreatePipelineRequest[]> FinishFromCapabilitiesAsync(
        IRenderedComponent<PipelineSetupWizard> cut, IRenderedComponent<WizardDialog> wizard)
    {
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase)));
        return _handler.RequestDetails
            .Where(request => request.Method == HttpMethod.Post.Method
                && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
            .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
    }

    private void NothingMissing() => _handler.SetJsonResponse(
        HttpMethod.Post,
        "api/pipelines/setup/readiness",
        new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-qa-extended", "application-security-history", "application-security-external",
            "application-candidate", "application-nightly", "application-deploy-prod", "application-release-fast",
            "package-telemetry", "package-web-analytics-dotnet", "package-web-analytics-browser",
            "publish-observability-packages"
        }.Select(name => new PipelineTemplateSummaryDto
        {
            Id = name.GetHashCode(StringComparison.Ordinal),
            Name = name,
            Version = 1
        })
    ];
}
