// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R-105 (user decision 2026-10-25): the quality pipeline the wizard creates is written without the steps of a
/// language the project does not use, and without "Publish lint" (the wizard writes no command that produces a lint report),
/// so no step is shown skipped at every run. The languages are pre-ticked from what the repository holds and the user changes
/// them. These tests cover the C# that decides and writes; that the engine then leaves those stages out is covered on the
/// resolver, and a real run of the generated pipeline is not proved here.
/// </summary>
public sealed class PipelineSetupQualityLanguagesTests : BunitContext
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupQualityLanguagesTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineSetupYamlInput Input(PipelineSetupLanguages? languages) => new(
        "atlas", (true, true, false), "package-web-analytics-dotnet",
        Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false,
        ["ci", "quality"], "develop", languages);

    private static List<Dictionary<object, object>> Stages(string yaml) =>
        [.. ((List<object>)Yaml.Deserialize<Dictionary<object, object>>(yaml)["stages"]).Cast<Dictionary<object, object>>()];

    private static string[] Removed(string yaml) =>
        [.. Stages(yaml).Where(stage => stage.ContainsKey("remove")).Select(stage => (string)stage["name"])];

    /// <summary>A quality template reduced to what the wizard reads: a "Lint source" stage that also carries a property
    /// the wizard knows nothing about (a condition and a timeout), to prove the rewritten stage is the template's own.</summary>
    private const string TemplateYaml = """
        name: application-quality-template
        stages:
          - name: Lint source
            group: Quality
            execution_role: build
            os: linux
            depends_on: [Measure complexity]
            condition: "eq(variables['ENABLED'], 'true')"
            approval_timeout_minutes: 45
            steps:
              - name: Lint Python
                condition: "eq(variables['HAS_PYTHON'], 'true')"
                type: scanner
                scanner: ruff
              - name: Check Java quality
                condition: "eq(variables['HAS_JAVA'], 'true')"
                type: scanner
                scanner: pmd-java
        """;

    private static string Quality(PipelineSetupLanguages languages) => PipelineSetupYamlBuilder.Build(
        "atlas-quality", "application-quality", 1,
        Input(languages) with
        {
            LintSourceStage = languages.UnwantedLintStep is { } step ? PipelineSetupLanguages.RestatedLintSource(TemplateYaml, step) : null
        });

    [Fact]
    public void DotNetOnly_LeavesOutPublishLintAndTheLintSourceStage()
    {
        var yaml = Quality(new PipelineSetupLanguages(true, false, false));

        Assert.Equal(["Publish lint", "Lint source"], Removed(yaml));
        Assert.Equal(2, Stages(yaml).Count);
    }

    [Fact]
    public void WithoutDotNet_LeavesOutTheComplexityAndTheSdkStagesToo()
    {
        var yaml = Quality(new PipelineSetupLanguages(false, true, true));

        Assert.Equal(["Publish lint", "Find pinned SDK", "Pinned SDK", "Measure complexity"], Removed(yaml));
        Assert.DoesNotContain(Stages(yaml), stage => (string)stage["name"] == "Lint source");
    }

    [Fact]
    public void EveryLanguage_LeavesOutPublishLintOnly()
    {
        Assert.Equal(["Publish lint"], Removed(Quality(new PipelineSetupLanguages(true, true, true))));
    }

    [Fact]
    public void NoLanguage_LeavesOutEveryLanguageStage()
    {
        Assert.Equal(
            ["Publish lint", "Find pinned SDK", "Pinned SDK", "Measure complexity", "Lint source"],
            Removed(Quality(new PipelineSetupLanguages(false, false, false))));
    }

    [Theory]
    [InlineData(true, false, "Check Java quality")]
    [InlineData(false, true, "Lint Python")]
    public void OneOfThePairOfLinters_RestatesTheLintSourceStageWithoutTheOtherStep(bool python, bool java, string unwantedStep)
    {
        var yaml = Quality(new PipelineSetupLanguages(true, python, java));

        var stage = Assert.Single(Stages(yaml), item => (string)item["name"] == "Lint source");
        Assert.False(stage.ContainsKey("remove"));
        // The template's own stage, read from the pinned version: every property it carries comes along, those the wizard
        // knows nothing about included, since the stage of the child replaces the template's.
        Assert.Equal("Quality", stage["group"]);
        Assert.Equal("build", stage["execution_role"]);
        Assert.Equal("linux", stage["os"]);
        Assert.Equal(["Measure complexity"], ((List<object>)stage["depends_on"]).Cast<string>());
        Assert.Equal("eq(variables['ENABLED'], 'true')", stage["condition"]);
        Assert.Equal("45", stage["approval_timeout_minutes"]);
        var step = Assert.Single((List<object>)stage["steps"]);
        Assert.Equal(unwantedStep, ((Dictionary<object, object>)step)["name"]);
        Assert.Equal("true", ((Dictionary<object, object>)step)["remove"]);
    }

    [Fact]
    public void ALintSourceStageTheWizardCouldNotRead_StopsTheWriting_InsteadOfWritingACopy()
    {
        Assert.Null(PipelineSetupLanguages.RestatedLintSource("name: t\nstages:\n  - name: Other\n    steps: []\n", "Lint Python"));
        Assert.Throws<InvalidOperationException>(() => new PipelineSetupLanguages(true, true, false).QualityStages());
    }

    [Fact]
    public void ALintSourceStageThatAlreadyHasNoSuchStep_NeedsNothingWritten()
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-quality", "application-quality", 1,
            Input(new PipelineSetupLanguages(true, true, false)) with
            {
                LintSourceStage = PipelineSetupLanguages.RestatedLintSource(
                    "name: t\nstages:\n  - name: Lint source\n    steps:\n      - name: Lint Python\n        shell: x\n", "Check Java quality")
            });

        Assert.Equal(["Publish lint"], Removed(yaml));
        Assert.Single(Stages(yaml));
    }

    [Fact]
    public void WithoutLanguages_OrForAnotherTemplate_NoStageIsWritten()
    {
        Assert.Contains("stages: []", PipelineSetupYamlBuilder.Build("atlas-quality", "application-quality", 1, Input(null)), StringComparison.Ordinal);
        Assert.Contains("stages: []",
            PipelineSetupYamlBuilder.Build("atlas-ci", "application-ci", 1, Input(new PipelineSetupLanguages(false, false, false))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheProposal_FollowsTheRepository_AndKeepsWhatItCouldNotTell()
    {
        // Not read at all (R-42): .NET from the stack, Python and Java never assumed.
        Assert.Equal(new PipelineSetupLanguages(true, false, false), PipelineSetupLanguages.Proposal(null, PipelineSetupStack.DotNet));
        Assert.Equal(new PipelineSetupLanguages(true, false, false),
            PipelineSetupLanguages.Proposal(new PipelineSetupDetectionDto(), PipelineSetupStack.DotNet));
        // Read to the end: what it holds.
        Assert.Equal(new PipelineSetupLanguages(false, true, false),
            PipelineSetupLanguages.Proposal(
                new PipelineSetupDetectionDto { HasDotNetProject = false, HasPython = true, HasJava = false }, PipelineSetupStack.DotNet));
        // A search the budget stopped (null) for Java: not ticked, found languages as they are.
        Assert.Equal(new PipelineSetupLanguages(true, false, false),
            PipelineSetupLanguages.Proposal(
                new PipelineSetupDetectionDto { HasDotNetProject = true, HasPython = false, HasJava = null }, PipelineSetupStack.DotNet));
    }

    [Fact]
    public void TheLanguagesAreUncertain_AsSoonAsOneAnswerIsMissing()
    {
        Assert.True(PipelineSetupLanguages.IsUncertain(null));
        Assert.True(PipelineSetupLanguages.IsUncertain(new PipelineSetupDetectionDto { HasDotNetProject = true, HasPython = false }));
        Assert.False(PipelineSetupLanguages.IsUncertain(
            new PipelineSetupDetectionDto { HasDotNetProject = true, HasPython = false, HasJava = false }));
    }

    private void Arrange(PipelineSetupDetectionDto detection)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod"
        }.Select((name, index) => new PipelineTemplateSummaryDto { Id = index + 1, Name = name, Version = 1 }).ToList());
        // The pinned version of the quality template (id 2), read when exactly one of Python and Java is used.
        _handler.SetJsonResponse("api/pipelines/templates/2/versions/1", new PipelineTemplateVersionDto { Version = 1, YamlContent = TemplateYaml });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness", new PipelineSetupReadinessDto
        {
            RepositoryInspected = true,
            InspectedBranch = "develop",
            Detection = detection
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync(PipelineSetupDetectionDto detection)
    {
        Arrange(detection);
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='setup-option-lang-dotnet']")));
        return cut;
    }

    private static bool IsTicked(IRenderedComponent<PipelineSetupWizard> cut, string option) =>
        cut.Find($"[data-testid='setup-option-{option}']").ClassList.Contains("omni-selectable-card--selected");

    private async Task<string> CreatedQualityYamlAsync(IRenderedComponent<PipelineSetupWizard> cut)
    {
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        CreatePipelineRequest[] Created() => [.. _handler.RequestDetails
            .Where(request => request.Method == HttpMethod.Post.Method
                && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
            .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)];
        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        return Created().Single(request => request.Name == "atlas-quality").YamlDefinition;
    }

    [Fact]
    public async Task TheCapabilitiesStep_PreTicksTheLanguagesTheRepositoryHolds_AndTheQualityPipelineLeavesOutTheOthers()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto
        {
            HasDotNetSolution = true,
            HasDotNetTestProject = true,
            HasComposeFile = true,
            HasDockerRunner = true,
            HasDotNetProject = true,
            HasPython = true,
            HasJava = false
        });

        Assert.True(IsTicked(cut, "lang-dotnet"));
        Assert.True(IsTicked(cut, "lang-python"));
        Assert.False(IsTicked(cut, "lang-java"));

        var yaml = await CreatedQualityYamlAsync(cut);

        var stage = Assert.Single(Stages(yaml), item => (string)item["name"] == "Lint source");
        Assert.Equal("Check Java quality", ((Dictionary<object, object>)Assert.Single((List<object>)stage["steps"]))["name"]);
        Assert.Equal(["Publish lint"], Removed(yaml));
    }

    [Fact]
    public async Task ALanguageTheUserChangesAfterTheProposal_IsWhatTheQualityPipelineKeeps()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto
        {
            HasDotNetSolution = true,
            HasDotNetTestProject = true,
            HasDotNetProject = true,
            HasPython = true,
            HasJava = false
        });

        await cut.Find("[data-testid='setup-option-lang-python']").ClickAsync(new MouseEventArgs());
        await cut.Find("[data-testid='setup-option-lang-dotnet']").ClickAsync(new MouseEventArgs());

        Assert.False(IsTicked(cut, "lang-python"));
        Assert.False(IsTicked(cut, "lang-dotnet"));
        var yaml = await CreatedQualityYamlAsync(cut);
        Assert.Equal(["Publish lint", "Find pinned SDK", "Pinned SDK", "Measure complexity", "Lint source"], Removed(yaml));
    }

    [Fact]
    public async Task ARepositoryThatWasNotRead_TicksOnlyDotNet_AndAsksTheUserToConfirm()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto());

        Assert.True(IsTicked(cut, "lang-dotnet"));
        Assert.False(IsTicked(cut, "lang-python"));
        Assert.False(IsTicked(cut, "lang-java"));
        Assert.Contains("PipelineQualityLanguagesUncertain",
            cut.Find("[data-testid='setup-quality-languages-uncertain']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryReadToTheEnd_AsksNothing()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto { HasDotNetProject = true, HasPython = false, HasJava = false });

        Assert.Empty(cut.FindAll("[data-testid='setup-quality-languages-uncertain']"));
    }

    [Fact]
    public async Task ASearchTheBudgetStopped_LeavesThatLanguageUnticked_WhileTheOthersFollowTheRepository()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto { HasDotNetProject = true, HasPython = false, HasJava = null });

        Assert.False(IsTicked(cut, "lang-python"));
        Assert.False(IsTicked(cut, "lang-java"));
        Assert.NotEmpty(cut.FindAll("[data-testid='setup-quality-languages-uncertain']"));
    }

    [Fact]
    public async Task AQualityTemplateThatCannotBeRead_StopsTheCreation_AndWritesNoCopyOfItsStage()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto { HasDotNetProject = true, HasPython = true, HasJava = false });
        _handler.SetResponse("api/pipelines/templates/2/versions/1", System.Net.HttpStatusCode.NotFound);
        _handler.SetResponse(HttpMethod.Delete, "api/pipelines/", System.Net.HttpStatusCode.NoContent);

        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Method == "DELETE"));
        Assert.DoesNotContain(_handler.RequestDetails, request =>
            request.Method == "POST" && request.Body is not null && request.Body.Contains("\"name\":\"atlas-quality\"", StringComparison.Ordinal));
    }

    private const string AddressBase = "projectId=7&prefix=atlas&architecture=application&sameProject=no&step=summary&options=";
    private const string AddressPipelines = "ci,quality,security,qa,candidate,nightly,production,backend";

    private async Task<string> QualityYamlFromAddressAsync(string languageOptions)
    {
        Arrange(new PipelineSetupDetectionDto());
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"http://localhost/pipelines/setup?{AddressBase}{Uri.EscapeDataString(AddressPipelines + languageOptions)}");
        var reopened = Render<PipelineSetupWizard>();
        reopened.WaitForAssertion(() => Assert.Contains("setup-summary", reopened.Markup, StringComparison.Ordinal));
        await reopened.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        CreatePipelineRequest[] Created() => [.. _handler.RequestDetails
            .Where(request => request.Method == HttpMethod.Post.Method
                && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
            .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)];
        reopened.WaitForAssertion(() => Assert.Contains(Created(), request => request.Name == "atlas-quality"));
        return Created().Last(request => request.Name == "atlas-quality").YamlDefinition;
    }

    [Fact]
    public async Task ASummaryAddressWrittenBeforeLanguagesExisted_KeepsTheProposal_InsteadOfUntickingEveryLanguage()
    {
        var yaml = await QualityYamlFromAddressAsync(string.Empty);

        // The proposal of an unread repository: .NET only, so Publish lint and the lint stage go.
        Assert.Equal(["Publish lint", "Lint source"], Removed(yaml));
    }

    [Fact]
    public async Task ASummaryAddressThatNamesNoLanguageOnPurpose_RestoresNoLanguage()
    {
        var yaml = await QualityYamlFromAddressAsync(",lang-none");

        Assert.Equal(["Publish lint", "Find pinned SDK", "Pinned SDK", "Measure complexity", "Lint source"], Removed(yaml));
    }

    [Fact]
    public async Task ASummaryAddressThatNamesLanguages_RestoresThem()
    {
        var yaml = await QualityYamlFromAddressAsync(",lang-dotnet,lang-java");

        var stage = Assert.Single(Stages(yaml), item => (string)item["name"] == "Lint source");
        Assert.Equal("Lint Python", ((Dictionary<object, object>)Assert.Single((List<object>)stage["steps"]))["name"]);
        Assert.Equal("eq(variables['ENABLED'], 'true')", stage["condition"]);
    }

    [Fact]
    public async Task WithoutTheQualityPipeline_TheLanguagesAreNotAsked()
    {
        var cut = await OpenCapabilitiesAsync(new PipelineSetupDetectionDto());

        await cut.Find("[data-testid='setup-option-quality']").ClickAsync(new MouseEventArgs());

        Assert.Empty(cut.FindAll("[data-testid='setup-option-lang-dotnet']"));
    }
}
