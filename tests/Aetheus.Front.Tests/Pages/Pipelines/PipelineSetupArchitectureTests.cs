// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Xml.Linq;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R1-18 / R1-31 / R1-33: the setup wizard's second step asks the type of what the pipelines
/// build and check, and the type decides the pipelines proposed and the variables written.
/// </summary>
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
        Assert.Empty(cut.FindAll(".pipeline-setup-option"));
        Assert.Empty(cut.FindAll("[data-testid='setup-same-project']"));
        Assert.False(wizard.Instance.CanAdvance);
        Assert.Contains("PipelineArchitectureQuestion", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>R1-31 decision: no type name for the one-project case, a yes/no question with no default.</summary>
    [Fact]
    public async Task Application_AsksWhetherTheServerServesTheInterface_WithNoDefaultAnswer()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await Choose(cut, "application");

        var question = cut.Find("[data-testid='setup-same-project']");
        Assert.Contains("PipelineSameProjectQuestion", question.TextContent, StringComparison.Ordinal);
        var radios = question.QuerySelectorAll("input[type='radio']");
        Assert.Equal(2, radios.Length);
        Assert.All(radios, radio => Assert.False(radio.HasAttribute("checked")));
        Assert.Empty(cut.FindAll(".pipeline-setup-option"));
        Assert.False(wizard.Instance.CanAdvance);
    }

    [Fact]
    public async Task SameProjectYes_ShowsOnlyTheDatabaseDetail_AndClaimsNoSeparateFrontend()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: true);

        Assert.True(wizard.Instance.CanAdvance);
        var option = Assert.Single(cut.FindAll(".pipeline-setup-option"));
        Assert.Contains("Database", option.TextContent, StringComparison.Ordinal);
        Assert.Contains("PipelineSameProjectYesDetail", cut.Markup, StringComparison.Ordinal);
        await cut.InvokeAsync(() => option.QuerySelector("input")!.Change(true));

        var requests = await FinishAsync(cut, wizard);

        Assert.Equal(
            ["atlas-ci", "atlas-quality", "atlas-security", "atlas-qa", "atlas-candidate", "atlas-deploy-prod"],
            requests.Select(request => request.Name));
        Assert.All(requests, request =>
        {
            Assert.Contains("APPLICATION_HAS_FRONTEND: \"false\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_BACKEND: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
            Assert.Contains("APPLICATION_HAS_DATABASE: \"true\"", request.YamlDefinition, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SameProjectNo_KeepsTheFrontendBackendAndDatabaseDetail()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);

        Assert.Equal(3, cut.FindAll(".pipeline-setup-option").Count);
        Assert.True(wizard.Instance.CanAdvance);
    }

    [Fact]
    public async Task Package_ProposesTheRepositoryPackageSet_AndNoDeliveryPipeline()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await Choose(cut, "package");
        await cut.InvokeAsync(wizard.Instance.NextStep);

        var titles = cut.FindAll(".pipeline-setup-option strong").Select(title => title.TextContent).ToArray();
        Assert.Equal(["CI", "Quality", "Security", "Packages"], titles);

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

    /// <summary>R1-33: an external repository gets the one pipeline that needs no file in it.</summary>
    [Fact]
    public async Task External_ProposesAndCreatesOnlyTheMinimalSecurityPipeline()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();

        await Choose(cut, "external");
        Assert.True(wizard.Instance.CanAdvance);
        Assert.Empty(cut.FindAll("[data-testid='setup-same-project']"));
        await cut.InvokeAsync(wizard.Instance.NextStep);

        var titles = cut.FindAll(".pipeline-setup-option strong").Select(title => title.TextContent).ToArray();
        Assert.Equal(["ExternalSecurityPipeline"], titles);
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

        await cut.InvokeAsync(() => OptionInput(cut, "ExternalSecurityPipeline").Change(false));

        Assert.False(wizard.Instance.CanAdvance);
    }

    [Fact]
    public async Task ChoicesHiddenByTheType_CreateNothing()
    {
        var (cut, wizard) = await OpenArchitectureStepAsync();
        // Nightly chosen under an application, then the type becomes a package.
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(() => OptionInput(cut, "Nightly").Change(true));
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
        Assert.Contains("PipelineArchitectureApplication · PipelineSameProjectSummary · Database", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("atlas-nightly", cut.Markup, StringComparison.Ordinal);
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

    /// <summary>R1-18: the technology is a list that only holds what is implemented: .NET alone today.</summary>
    [Fact]
    public void Stacks_OfferOnlyWhatIsImplemented()
    {
        var stack = Assert.Single(PipelineSetupStack.All);
        Assert.Equal("package-web-analytics-dotnet", stack.AnalyticsPackageTemplate);
        Assert.Same(PipelineSetupStack.DotNet, PipelineSetupStack.Find("DOTNET"));
        Assert.Null(PipelineSetupStack.Find("node"));
    }

    /// <summary>R1-31: the step is named after what is built and checked, and the one-project case is the
    /// question the user chose, never a type name.</summary>
    [Theory]
    [InlineData("AppStrings.resx", "Pipeline architecture", "Is the interface served by the same project as the server? (e.g. Blazor Server)")]
    [InlineData("AppStrings.fr-FR.resx", "Architecture du pipeline", "L’interface est servie par le même projet que le serveur ? (ex. Blazor Server)")]
    public void StepNameAndSameProjectQuestion_AreTheChosenOnes(string file, string title, string question)
    {
        var document = XDocument.Load(Path.Combine(
            Aetheus.Front.Tests.Architecture.RepositoryScan.Root, "src", "Aetheus.Front", "Resources", file));
        var values = document.Root!.Elements("data")
            .ToDictionary(element => (string)element.Attribute("name")!, element => element.Element("value")!.Value);

        Assert.Equal(title, values["PipelineArchitectureTitle"]);
        Assert.Equal(question, values["PipelineSameProjectQuestion"]);
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

    private static AngleSharp.Dom.IElement OptionInput(IRenderedComponent<PipelineSetupWizard> cut, string title) =>
        cut.FindAll(".pipeline-setup-option")
            .Single(option => option.QuerySelector("strong")!.TextContent == title)
            .QuerySelector("input")!;

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
