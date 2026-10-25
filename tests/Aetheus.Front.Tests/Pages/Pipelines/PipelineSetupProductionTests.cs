// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R2-28: ticking production in the capabilities step creates the production pipeline, whatever the
/// project holds. The step asks nothing about a deployment target. The summary prepares the production deployment
/// from the repository (ADR-057): the cases below that give it nothing to read (the default of these tests) are the
/// ones where the pipeline is created all the same, with no server and no Compose file written into it.</summary>
public sealed class PipelineSetupProductionTests : BunitContext
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupProductionTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void Arrange()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync()
    {
        Arrange();
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        return cut;
    }

    private static Task ClickAsync(IRenderedComponent<PipelineSetupWizard> cut, string testId) =>
        cut.Find($"[data-testid='{testId}']").ClickAsync(new MouseEventArgs());

    private async Task<CreatePipelineRequest[]> FinishAsync(IRenderedComponent<PipelineSetupWizard> cut, int expected)
    {
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(expected, Created().Length));
        return Created();
    }

    private CreatePipelineRequest[] Created() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

    private static Dictionary<object, object> Parse(string yaml) => Yaml.Deserialize<Dictionary<object, object>>(yaml);

    private static PipelineSetupYamlInput Input() => new(
        "atlas", (true, true, false), "package-web-analytics-dotnet",
        Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false,
        ["ci", "deploy-prod"]);

    /// <summary>The proof of R2-28: a project with no agent server, production ticked, the deployment pipeline
    /// is created, wired to the candidate, and written with no server and no empty command.</summary>
    [Fact]
    public async Task ProductionTicked_OnAProjectWithNoServer_CreatesTheDeployProdPipeline()
    {
        var cut = await OpenCapabilitiesAsync();

        var created = await FinishAsync(cut, 7);

        var deploy = Assert.Single(created, request => request.Name == "atlas-deploy-prod");
        var yaml = Parse(deploy.YamlDefinition);
        Assert.Equal("application-deploy-prod@1", yaml["extends"]);
        Assert.False(yaml.ContainsKey("parameters"));
        Assert.DoesNotContain("deployServer", deploy.YamlDefinition, StringComparison.Ordinal);
        // The command is the template's own default: the wizard writes none, and above all never an empty one
        // (an empty value would replace the default and the template would refuse to run).
        Assert.DoesNotContain("APPLICATION_DEPLOY_PROD_COMMANDS", deploy.YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_DEPLOY_PIPELINE: \"atlas-deploy-prod\"",
            Assert.Single(created, request => request.Name == "atlas-candidate").YamlDefinition, StringComparison.Ordinal);
        // The libraries and vaults the production template declares (`<project>.prod`) are read from the template
        // names the readiness check receives: the deployment template is among them although no server is known.
        Assert.Contains(_handler.RequestDetails, request =>
            request.Url.EndsWith("api/pipelines/setup/readiness", StringComparison.OrdinalIgnoreCase)
            && request.Body!.Contains("application-deploy-prod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCapabilitiesStep_AsksNothingAboutADeploymentTarget_AndNeverReadsTheServers()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.Contains("setup-option-production", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='setup-deploy-target']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-deploy-none']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-deploy-docker']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-deploy-server']"));
        Assert.DoesNotContain(_handler.RequestDetails, request => request.Url.Contains("/servers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutProduction_NoProductionPipelineIsCreated_AndTheCandidateNamesNone()
    {
        var cut = await OpenCapabilitiesAsync();

        await ClickAsync(cut, "setup-option-production");
        var created = await FinishAsync(cut, 6);

        Assert.DoesNotContain(created, request => request.Name == "atlas-deploy-prod");
        Assert.All(created, request =>
            Assert.DoesNotContain("APPLICATION_DEPLOY_PIPELINE", request.YamlDefinition, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSummary_ListsTheProductionPipeline_AndHasNoDeploymentTargetLine()
    {
        var cut = await OpenCapabilitiesAsync();

        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);

        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("atlas-deploy-prod", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='setup-summary-deploy']"));
    }

    [Fact]
    public async Task TheSummaryAddress_CarriesNoDeploymentChoice()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = await OpenCapabilitiesAsync();

        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);

        Assert.Contains("&step=summary", nav.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy=", nav.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("deployServer", nav.Uri, StringComparison.Ordinal);
    }

    /// <summary>An address written before R2-28 still opens: its deployment words are ignored and production,
    /// being ticked, creates its pipeline.</summary>
    [Theory]
    [InlineData("deploy=docker&deployServer=atlas-prod-eu")]
    [InlineData("deploy=none")]
    public void AnOldAddress_StillOpensTheSummary_WithTheProductionPipeline(string oldQuery)
    {
        Arrange();
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "http://localhost/pipelines/setup?projectId=7&prefix=atlas&options=ci%2Cproduction%2Cbackend"
            + $"&architecture=application&sameProject=no&{oldQuery}&step=summary");

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("atlas-ci", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("atlas-deploy-prod", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheDeployProdYaml_WithNoServerKnown_LeavesTheTemplatesRequiredParameterToAskForIt()
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-deploy-prod", "application-deploy-prod", 1, Input());

        Assert.DoesNotContain("deployServer", yaml, StringComparison.Ordinal);
        Assert.False(Parse(yaml).ContainsKey("parameters"));
    }

    [Fact]
    public void TheFastReleaseYaml_WithNoServerKnown_LeavesTheTemplatesRequiredParameterToAskForIt()
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-release-fast", "application-release-fast", 1, Input());

        Assert.DoesNotContain("deployServer", yaml, StringComparison.Ordinal);
        Assert.False(Parse(yaml).ContainsKey("parameters"));
    }

    // Recette R-21: the server is chosen from the project's servers, the production environment's one by default.
    [Theory]
    [InlineData("atlas-deploy-prod", "application-deploy-prod")]
    [InlineData("atlas-release-fast", "application-release-fast")]
    public void TheDeploymentYaml_OffersTheProjectsServersAsAChoice_WithTheEnvironmentServerAsDefault(
        string pipeline, string template)
    {
        var yaml = PipelineSetupYamlBuilder.Build(pipeline, template, 1, Input(),
            new PipelineSetupProductionInput("compose.yml", "replace", false, "vpssim", ["backup", "vpssim"]));

        var parameter = (Dictionary<object, object>)((List<object>)Parse(yaml)["parameters"]).Single();
        Assert.Equal("deployServer", parameter["name"]);
        Assert.Equal("choice", parameter["type"]);
        Assert.Equal("true", parameter["required"]);
        Assert.Equal("vpssim", parameter["default"]);
        Assert.Equal(["backup", "vpssim"], ((List<object>)parameter["allowed_values"]).Cast<string>());
        Assert.Equal("Serveur de production", parameter["display_name_fr"]);
        Assert.DoesNotContain("runner", (string)parameter["description"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDeploymentYaml_WithServersButNoDefault_OffersTheChoiceWithoutGuessingOne()
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-deploy-prod", "application-deploy-prod", 1, Input(),
            new PipelineSetupProductionInput("compose.yml", "replace", false, null, ["a", "b"]));

        var parameter = (Dictionary<object, object>)((List<object>)Parse(yaml)["parameters"]).Single();
        Assert.False(parameter.ContainsKey("default"));
        Assert.Equal(["a", "b"], ((List<object>)parameter["allowed_values"]).Cast<string>());
    }

    // Recette R-19: the QA pipeline gets the Compose file of its disposable stack, and nothing of the deployment.
    [Fact]
    public void TheQaYaml_GetsTheComposeFileOnly()
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-qa", "application-qa", 1, Input(),
            new PipelineSetupProductionInput("deploy/compose/remote.compose.yml", "replace", true, "vpssim", ["vpssim"]));

        var parsed = Parse(yaml);
        var variables = (Dictionary<object, object>)parsed["variables"];
        Assert.Equal("deploy/compose/remote.compose.yml", variables["COMPOSE_FILE"]);
        Assert.False(variables.ContainsKey("DEPLOY_STRATEGY"));
        Assert.False(variables.ContainsKey("DEPLOY_HAS_API"));
        Assert.False(parsed.ContainsKey("parameters"));
    }

    // Recette R-5: the end of the wizard names every entry the production pipeline reads that is still empty.
    [Fact]
    public void ThePendingValues_NameTheEmptyEntriesAndTheUnallocatedPorts_Only()
    {
        var result = new PipelineProductionApplyResultDto
        {
            Entries =
            [
                new ProductionAppliedEntryDto { Name = "SMTP_HOST", Outcome = ProductionEntryOutcome.NeedsValue },
                new ProductionAppliedEntryDto { Name = "DB_PASSWORD", Secret = true, Outcome = ProductionEntryOutcome.Generated },
                new ProductionAppliedEntryDto { Name = "PORT_FRONT", Outcome = ProductionEntryOutcome.PortUnavailable },
                new ProductionAppliedEntryDto { Name = "DOMAIN", Outcome = ProductionEntryOutcome.NeedsValue },
                new ProductionAppliedEntryDto { Name = "APPNAME", Outcome = ProductionEntryOutcome.Kept }
            ]
        };

        Assert.Equal("DOMAIN, PORT_FRONT, SMTP_HOST", PipelineSetupProduction.PendingValues(result));
        Assert.Empty(PipelineSetupProduction.PendingValues(new PipelineProductionApplyResultDto()));
    }

    [Fact]
    public void TheCiYaml_GetsNothingOfTheProductionSetup()
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-ci", "application-ci", 1, Input(),
            new PipelineSetupProductionInput("compose.yml", "replace", true, "vpssim", ["vpssim"]));

        Assert.DoesNotContain("COMPOSE_FILE", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("deployServer", yaml, StringComparison.Ordinal);
    }

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
