// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>ADR-057: the summary of the setup wizard prepares the production deployment from the Compose file of
/// the repository, and the production pipeline it creates carries what was prepared: the Compose file, the strategy
/// and the server. The setup is applied before the pipelines exist, and a refusal creates none.</summary>
public sealed class PipelineSetupProductionPanelTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupProductionPanelTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineProductionAnalysisDto ReadableAnalysis(params string[] servers) => new()
    {
        Readable = true,
        Branch = "develop",
        ComposeFiles = ["deploy/compose/remote.compose.yml", "deploy/compose/dev.compose.yml"],
        ComposeFile = "deploy/compose/remote.compose.yml",
        Servers = [.. servers.Select((name, index) => new ProductionServerDto { Id = index + 1, Name = name })],
        // As the backend proposes it with no production environment: the only server, none among several.
        DefaultServer = servers.Length == 1 ? servers[0] : null,
        DeclaresBackPort = true,
        Variables =
        [
            new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port },
            new() { Name = "DB_PASSWORD", Kind = ProductionVariableKind.GeneratedSecret, Secret = true },
            new() { Name = "APPNAME", Kind = ProductionVariableKind.DefaultValue, DefaultValue = "atlas" },
            new() { Name = "SMTP_HOST", Kind = ProductionVariableKind.NeedsValue },
            new() { Name = "SMTP_CREDENTIAL", Kind = ProductionVariableKind.NeedsValue, Secret = true },
            new() { Name = "ADMIN_EMAIL", Kind = ProductionVariableKind.NeedsValue, Exists = true }
        ]
    };

    private void Arrange(PipelineProductionAnalysisDto analysis)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, analysis);
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = "deploy/compose/remote.compose.yml",
            // The server the request named: the only one the project has, else none was chosen.
            Server = analysis.Servers.Count == 1 ? analysis.Servers[0].Name : string.Empty,
            Strategy = "replace",
            DeclaresApi = true,
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "Atlas configuration", Slug = "atlas-config", Created = true }
        });
    }

    /// <summary>The wizard on its summary step, the production pipeline being ticked by default.</summary>
    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync(PipelineProductionAnalysisDto analysis)
    {
        Arrange(analysis);
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static Task FinishAsync(IRenderedComponent<PipelineSetupWizard> cut) =>
        cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());

    private IEnumerable<(string Url, string? Body)> Posts() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method)
        .Select(request => (request.Url, request.Body));

    private CreatePipelineRequest[] Created() => Posts()
        .Where(post => post.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(post => JsonSerializer.Deserialize<CreatePipelineRequest>(post.Body!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

    private static Dictionary<object, object> Parse(string yaml) => Yaml.Deserialize<Dictionary<object, object>>(yaml);

    [Fact]
    public async Task TheSummary_ShowsTheComposeFileTheStrategyTheServerAndTheVariablesToPrepare()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='setup-production']")));
        Assert.Equal("vps", cut.Find("[data-testid='setup-production-server-name']").TextContent);
        Assert.Equal("PipelineSetupProdReplace", cut.Find("[data-testid='setup-production-strategy']").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid='setup-production-variables']"));
        // What the deployment provides itself or defaults is not asked; what is generated or conventional is only told.
        Assert.Empty(cut.FindAll("#setup-production-value-DB_PASSWORD"));
        Assert.Empty(cut.FindAll("#setup-production-value-APPNAME"));
        // A value only the operator knows is a field; one the project already holds is not asked again.
        Assert.NotEmpty(cut.FindAll("#setup-production-value-SMTP_HOST"));
        Assert.Empty(cut.FindAll("#setup-production-value-ADMIN_EMAIL"));
    }

    [Fact]
    public async Task ASecretOnlyTheOperatorKnows_IsAskedInAPasswordField_NotInAPlainOne()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#setup-production-value-SMTP_CREDENTIAL")));
        Assert.Contains("omni-password__input--masked", cut.Find("#setup-production-value-SMTP_CREDENTIAL").ClassList);
        Assert.DoesNotContain("omni-password__input--masked", cut.Find("#setup-production-value-SMTP_HOST").ClassList);
    }

    [Fact]
    public async Task Finishing_AppliesTheProductionSetupFirst_ThenWritesItsResultIntoTheProductionPipelineOnly()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"));
        await cut.Find("#setup-production-value-SMTP_HOST").InputAsync(new ChangeEventArgs { Value = "smtp.example.test" });

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        var posts = Posts().Select(post => post.Url).ToList();
        var applied = posts.FindIndex(url => url.EndsWith(Apply, StringComparison.OrdinalIgnoreCase));
        var firstPipeline = posts.FindIndex(url => url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase));
        Assert.True(applied >= 0 && applied < firstPipeline, "the variables, ports and files exist before the first pipeline.");

        var request = JsonSerializer.Deserialize<PipelineProductionApplyRequest>(
            Posts().Single(post => post.Url.EndsWith(Apply, StringComparison.OrdinalIgnoreCase)).Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(7, request.ProjectId);
        Assert.Equal("deploy/compose/remote.compose.yml", request.ComposeFile);
        Assert.Equal("vps", request.Server);
        // Only what the operator typed for a variable that needs one: never a secret read back, never a default.
        Assert.Equal(new Dictionary<string, string> { ["SMTP_HOST"] = "smtp.example.test" }, request.Values);

        var deploy = Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition);
        var variables = (Dictionary<object, object>)deploy["variables"];
        Assert.Equal("deploy/compose/remote.compose.yml", variables["COMPOSE_FILE"]);
        Assert.Equal("replace", variables["DEPLOY_STRATEGY"]);
        Assert.Equal("true", variables["DEPLOY_HAS_API"]);
        var parameter = (Dictionary<object, object>)((List<object>)deploy["parameters"]).Single();
        Assert.Equal("deployServer", parameter["name"]);
        Assert.Equal("vps", parameter["default"]);
        // The QA pipeline deploys its disposable stack from the same Compose file (R-19); no other one reads it.
        var qa = (Dictionary<object, object>)Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-qa").YamlDefinition)["variables"];
        Assert.Equal("deploy/compose/remote.compose.yml", qa["COMPOSE_FILE"]);
        Assert.All(Created().Where(pipeline => pipeline.Name is not ("atlas-deploy-prod" or "atlas-qa")),
            pipeline => Assert.DoesNotContain("COMPOSE_FILE", pipeline.YamlDefinition, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithSeveralServers_NoneIsGuessed_AndTheRunAsksForOneAmongThem()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps-a", "vps-b"));

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        var deploy = Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition);
        var parameter = (Dictionary<object, object>)((List<object>)deploy["parameters"]).Single();
        Assert.Equal("deployServer", parameter["name"]);
        Assert.False(parameter.ContainsKey("default"));
        Assert.Equal(["vps-a", "vps-b"], ((List<object>)parameter["allowed_values"]).Cast<string>());
        Assert.True(((Dictionary<object, object>)deploy["variables"]).ContainsKey("COMPOSE_FILE"));
    }

    [Fact]
    public async Task WhenTheRepositoryCannotBeRead_ThePipelineIsCreatedAllTheSame_WithNoComposeFileClaimed()
    {
        var cut = await OpenSummaryAsync(new PipelineProductionAnalysisDto { Readable = false });

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='setup-production-unreadable']")));
        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.DoesNotContain(Posts(), post => post.Url.EndsWith(Apply, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("COMPOSE_FILE", Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition,
            StringComparison.Ordinal);
    }

    private const string Repair = "api/pipelines/setup/production-repair";

    private static ProductionRefusedNameDto RefusedEnv() => new()
    {
        File = ".pipeline/configs/env/production.env",
        Line = 4,
        Variable = "ENV",
        Name = "ENV",
        ScopedName = "DEPLOY_ENV"
    };

    /// <summary>Recette R-5: an environment sample written before the fix still names a refused variable; the wizard
    /// says which file, which line and which name, in French, with one action.</summary>
    [Fact]
    public async Task ARefusedNameInTheExistingSample_IsShownPlainly_WithOneActionToCorrectIt()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps") with { RefusedEnvNames = [RefusedEnv()] });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-production-refused']")));
        var text = cut.Find("[data-testid='setup-production-refused']").TextContent;
        // The localizer stub returns the key: the text is French in the resources, and the file is named.
        Assert.Contains("PipelineSetupProdRefusedText", text, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupProdRefusedLine", text, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid='setup-production-refused-fix']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-production-repaired']"));
    }

    /// <summary>The sample lives in the configuration repository: an unreadable source repository does not hide it.</summary>
    [Fact]
    public async Task ARefusedNameInTheExistingSample_IsShown_EvenWhenTheSourceRepositoryCannotBeRead()
    {
        var cut = await OpenSummaryAsync(new PipelineProductionAnalysisDto { Readable = false, RefusedEnvNames = [RefusedEnv()] });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-production-refused']")));
        Assert.Single(cut.FindAll("[data-testid='setup-production-unreadable']"));
        Assert.Single(cut.FindAll("[data-testid='setup-production-refused-fix']"));
    }

    [Fact]
    public async Task ASoundSample_ShowsNoWarning_AndNoAction()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"));

        Assert.Empty(cut.FindAll("[data-testid='setup-production-refused']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-production-refused-fix']"));
    }

    [Fact]
    public async Task TheAction_MendsTheSampleOnce_ThenReadsTheProjectAgain_AndTheWarningGoes()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps") with { RefusedEnvNames = [RefusedEnv()] });
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-production-refused-fix']")));
        _handler.SetJsonResponse(HttpMethod.Post, Repair, new PipelineProductionRepairResultDto
        {
            File = ".pipeline/configs/env/production.env",
            Repaired = true,
            Mended = [RefusedEnv()],
            Entries = [new ProductionAppliedEntryDto { Name = "DEPLOY_ENV", Outcome = ProductionEntryOutcome.Renamed }]
        });
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, ReadableAnalysis("vps"));

        await cut.Find("[data-testid='setup-production-refused-fix']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid='setup-production-refused']"));
            Assert.Single(cut.FindAll("[data-testid='setup-production-repaired']"));
        });
        Assert.Single(Posts(), post => post.Url.EndsWith(Repair, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"projectId\":7", Posts().Single(post => post.Url.EndsWith(Repair, StringComparison.OrdinalIgnoreCase)).Body, StringComparison.Ordinal);
        // Mending creates no pipeline and applies no setup by itself.
        Assert.Empty(Created());
        Assert.DoesNotContain(Posts(), post => post.Url.EndsWith(Apply, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WhenTheMendingIsRefused_TheServersReasonIsShown_AndTheWarningStays()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps") with { RefusedEnvNames = [RefusedEnv()] });
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-production-refused-fix']")));
        _handler.SetJsonResponse(HttpMethod.Post, Repair,
            new Aetheus.Shared.Components.Shared.ApiError { Message = "Two values differ." }, HttpStatusCode.Conflict);

        await cut.Find("[data-testid='setup-production-refused-fix']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains("Two values differ.", cut.Find("[data-testid='setup-production-refused-error']").TextContent, StringComparison.Ordinal));
        Assert.Single(cut.FindAll("[data-testid='setup-production-refused']"));
    }

    [Fact]
    public async Task WhenTheSetupIsRefused_NoPipelineIsCreated()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"));
        _handler.SetResponse(HttpMethod.Post, Apply, HttpStatusCode.BadRequest);

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains(Posts(), post => post.Url.EndsWith(Apply, StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(Created());
    }

    [Fact]
    public async Task WithoutProduction_NothingIsReadFromTheRepository()
    {
        Arrange(ReadableAnalysis("vps"));
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.Find("[data-testid='setup-option-production']").ClickAsync(new MouseEventArgs());

        await cut.InvokeAsync(wizard.Instance.NextStep);

        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='setup-production']"));
        Assert.DoesNotContain(Posts(), post => post.Url.EndsWith(Analysis, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheProductionYaml_EscapesTheServerName_SoANameWithAQuoteNeverEndsTheValueEarly()
    {
        var input = new PipelineSetupYamlInput(
            "atlas", (true, true, false), "package-web-analytics-dotnet",
            Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false, ["deploy-prod"]);

        var yaml = PipelineSetupYamlBuilder.Build(
            "atlas-deploy-prod", "application-deploy-prod", 1, input,
            new PipelineSetupProductionInput("deploy/compose/remote.compose.yml", "bluegreen", true, "vps \"eu\""));

        var parsed = Parse(yaml);
        var parameter = (Dictionary<object, object>)((List<object>)parsed["parameters"]).Single();
        Assert.Equal("vps \"eu\"", parameter["default"]);
        Assert.Equal("bluegreen", ((Dictionary<object, object>)parsed["variables"])["DEPLOY_STRATEGY"]);
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod"
        }.Select(name => new PipelineTemplateSummaryDto { Id = name.GetHashCode(StringComparison.Ordinal), Name = name, Version = 1 })
    ];
}
