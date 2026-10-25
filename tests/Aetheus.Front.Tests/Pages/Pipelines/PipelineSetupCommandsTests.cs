// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R2-11 and R2-25: what the setup wizard writes into the pipelines it generates, and the fact that it
/// asks for no command and blocks nothing on them: the commands are the templates' own.</summary>
public sealed class PipelineSetupCommandsTests : BunitContext
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupCommandsTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineSetupYamlInput Input(string[] created, bool qa = true) => new(
        "atlas", (true, true, false), "package-web-analytics-dotnet",
        Rollback: true, Quality: true, Security: true, Qa: qa, QaExtended: false, SecurityHistory: false, created);

    private static Dictionary<string, object> Variables(string yaml) =>
        ((Dictionary<object, object>)Yaml.Deserialize<Dictionary<string, object>>(yaml)["variables"])
        .ToDictionary(pair => (string)pair.Key, pair => pair.Value);

    [Fact]
    public void OnlyThePipelinesReallyCreated_AreNamed()
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-ci", "application-ci", 3, Input(["ci", "quality"]));

        var variables = Variables(yaml);
        Assert.Equal("atlas-ci", variables["APPLICATION_CI_PIPELINE"]);
        Assert.Equal("atlas-quality", variables["APPLICATION_QUALITY_PIPELINE"]);
        Assert.DoesNotContain(variables.Keys, key => key is "APPLICATION_SECURITY_PIPELINE" or "APPLICATION_QA_PIPELINE"
            or "APPLICATION_CANDIDATE_PIPELINE" or "APPLICATION_DEPLOY_PIPELINE" or "APPLICATION_PACKAGE_TELEMETRY_PIPELINE");
        Assert.Contains("extends: application-ci@3", yaml, StringComparison.Ordinal);
    }

    /// <summary>User decision 2026-10-24: a generic template is always pinned to a number, never to "latest".</summary>
    [Theory]
    [InlineData("application-ci", 1)]
    [InlineData("application-candidate", 3)]
    [InlineData("application-deploy-prod", 12)]
    public void TheTemplateIsPinnedToTheNumberGiven_NeverToALatest(string template, int version)
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-pipeline", template, version, Input(["ci"]));

        Assert.Contains($"\nextends: {template}@{version}\n", $"\n{yaml}", StringComparison.Ordinal);
        Assert.DoesNotContain("latest", yaml, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A catalog that did not carry the version would give 0 or less: writing "@0" (or nothing) would leave the
    /// pipeline on whatever the server resolves later, so the creation is refused.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ATemplateWithoutARealVersionNumber_IsRefused_NotWrittenFloating(int version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PipelineSetupYamlBuilder.Build("atlas-pipeline", "application-ci", version, Input(["ci"])));

    /// <summary>R2-25: the wizard writes no command variable; the template's own default applies.</summary>
    [Theory]
    [InlineData("ci", "application-ci")]
    [InlineData("quality", "application-quality")]
    [InlineData("qa", "application-qa")]
    [InlineData("deploy-prod", "application-deploy-prod")]
    public void NoPipeline_CarriesACommandVariable(string suffix, string template)
    {
        var yaml = PipelineSetupYamlBuilder.Build($"atlas-{suffix}", template, 1, Input(["ci", "quality", "qa", "deploy-prod"]));

        Assert.DoesNotContain("_COMMANDS", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AChosenQa_IsEnabledInTheCandidate_AndAnUnchosenOneIsNot()
    {
        string[] created = ["ci", "qa", "candidate"];

        var chosen = Variables(PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3, Input(created)));
        var notChosen = Variables(PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3, Input(created, qa: false)));

        Assert.Equal("true", chosen["APPLICATION_QA_ENABLED"]);
        Assert.Equal("false", notChosen["APPLICATION_QA_ENABLED"]);
    }

    [Fact]
    public async Task TheCapabilitiesStep_AsksNoCommand()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.NotEmpty(cut.FindAll("[data-testid^='setup-option-']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-commands']"));
        Assert.Empty(cut.FindAll("[data-testid^='setup-commands-']"));
        Assert.Empty(cut.FindAll("textarea"));
    }

    /// <summary>R2-25: no notice about a missing command, no alert, and a production deployment without
    /// command no longer disables "Create the pipelines".</summary>
    [Fact]
    public async Task TheSummary_ShowsNoCommandNotice_AndDoesNotBlockTheCreation()
    {
        var cut = await OpenCapabilitiesAsync();
        var wizard = cut.FindComponent<WizardDialog>();

        await cut.InvokeAsync(wizard.Instance.NextStep);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("atlas-deploy-prod", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("PipelineSetupNotice", cut.Markup, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid^='setup-notice-']"));
            Assert.Empty(cut.FindAll(".omni-alert"));
            Assert.False(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
        });

        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        var byName = Created().ToDictionary(request => request.Name);
        Assert.Contains("atlas-deploy-prod", byName.Keys);
        Assert.All(byName.Values, request => Assert.DoesNotContain("_COMMANDS", request.YamlDefinition, StringComparison.Ordinal));
        Assert.Contains("APPLICATION_QA_ENABLED: \"true\"", byName["atlas-candidate"].YamlDefinition, StringComparison.Ordinal);
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync()
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
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        return cut;
    }

    private CreatePipelineRequest[] Created() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

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
