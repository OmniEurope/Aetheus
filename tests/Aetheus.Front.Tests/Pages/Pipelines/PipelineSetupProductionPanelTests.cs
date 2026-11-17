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
    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync(
        PipelineProductionAnalysisDto analysis, TaskCompletionSource<PipelineProductionAnalysisDto>? heldAnalysis = null, bool sameProject = false, string[]? strategies = null)
    {
        Arrange(analysis);
        // A reading of the repository that answers when the test lets it, to act while it is still running.
        if (heldAnalysis is not null)
            _handler.SetAsyncJsonResponse(HttpMethod.Post, Analysis, _ => heldAnalysis.Task);
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        // R-19: the strategy is chosen on the Capabilities step, before the summary reads the Compose file.
        foreach (var strategy in strategies ?? []) await ChooseStrategyAsync(cut, strategy);
        // The last step reads the repository before it returns: with that reading held back, it is not awaited.
        var summary = cut.InvokeAsync(wizard.Instance.NextStep);
        if (heldAnalysis is null) await summary;
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
        // Recette R-89: the strategy is a choice, no longer a deduction shown as text; R-23 (W8): the one this file follows
        // (its published ports, a replacement) is the one proposed.
        Assert.Equal("PipelineSetupProdReplace", cut.Find("[data-testid='setup-production-strategy-chosen']").TextContent);
        // R-40: what is generated is folded away; the values to fill are in view.
        Assert.NotEmpty(cut.FindAll("[data-testid='setup-production-generated']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='setup-production-to-fill']"));
        // What the deployment provides itself or defaults is not asked; what is generated or conventional is only told.
        Assert.Empty(cut.FindAll("#setup-production-value-DB_PASSWORD"));
        Assert.Empty(cut.FindAll("#setup-production-value-APPNAME"));
        // A value only the operator knows is a field; one the project already holds is not asked again.
        Assert.NotEmpty(cut.FindAll("#setup-production-value-SMTP_HOST"));
        Assert.Empty(cut.FindAll("#setup-production-value-ADMIN_EMAIL"));
    }

    private static Task ChooseStrategyAsync(IRenderedComponent<PipelineSetupWizard> cut, string strategy) =>
        PipelineSetupStrategyTileTests.ChooseStrategyAsync(cut, strategy);

    private PipelineProductionApplyRequest AppliedRequest() => JsonSerializer.Deserialize<PipelineProductionApplyRequest>(
        Posts().Single(post => post.Url.EndsWith(Apply, StringComparison.OrdinalIgnoreCase)).Body!,
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>The Compose file of W6 reads the published ports of a replacement (the default analysis of these tests);
    /// the other one reads the four colour ports of a blue-green deployment.</summary>
    private static PipelineProductionAnalysisDto BlueGreenAnalysis() => ReadableAnalysis("vps") with
    {
        DeclaresBackPort = false,
        DeclaresBlueGreen = true
    };

    /// <summary>Recette R-89 (user decision 2026-10-25) made the strategy a choice; recette R-19 (user decision 2026-11-09)
    /// makes the replacement of the services the default, whatever the Compose file declares (a file that reads the blue
    /// and green ports then gets the mismatch warning, never a blue-green deployment it was not asked for). The choice is
    /// made on the Capabilities step (PipelineSetupStrategyTileTests); the summary says which one is applied.</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task TheSummary_AppliesTheReplacement_ByDefault_AndWarnsWhenTheFileReadsColourPorts(bool blueGreenFile, int mismatches)
    {
        var cut = await OpenSummaryAsync(blueGreenFile ? BlueGreenAnalysis() : ReadableAnalysis("vps"));

        Assert.Equal("PipelineSetupProdReplace", cut.Find("[data-testid='setup-production-strategy-chosen']").TextContent);
        Assert.Equal(mismatches, cut.FindAll("[data-testid='setup-production-strategy-mismatch']").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finishing_SendsTheReplacement_WhenTheUserNeverTouchedTheStrategy(bool blueGreenFile)
    {
        var cut = await OpenSummaryAsync(blueGreenFile ? BlueGreenAnalysis() : ReadableAnalysis("vps"));

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.Equal("replace", AppliedRequest().Strategy);
    }

    [Fact]
    public async Task Finishing_SendsTheStrategyTheUserPicked()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"), strategies: ["bluegreen"]);
        Assert.Equal("PipelineSetupProdBlueGreen", cut.Find("[data-testid='setup-production-strategy-chosen']").TextContent);

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.Equal("bluegreen", AppliedRequest().Strategy);
    }

    /// <summary>The pipeline is written from what the setup applied, not from what the screen assumed.</summary>
    [Fact]
    public async Task TheProductionPipeline_CarriesTheStrategyTheSetupApplied()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"));
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = "deploy/compose/remote.compose.yml",
            Server = "vps",
            Strategy = "bluegreen",
            DeclaresApi = true,
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "Atlas configuration", Slug = "atlas-config" }
        });

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        var variables = (Dictionary<object, object>)Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition)["variables"];
        Assert.Equal("bluegreen", variables["DEPLOY_STRATEGY"]);
    }

    /// <summary>A strategy the Compose file does not follow is said, never imposed: the file belongs to the project and is
    /// adapted outside Aetheus, so the warning does not block the creation.</summary>
    [Fact]
    public async Task BlueGreenOnAFileThatReadsNoColourPorts_IsSaid_AndDoesNotBlockTheCreation()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"), strategies: ["bluegreen"]);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-production-strategy-mismatch']")));
        await FinishAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
    }

    [Fact]
    public async Task ReplaceOnAFileThatReadsTheColourPorts_IsSaid()
    {
        var cut = await OpenSummaryAsync(BlueGreenAnalysis());

        Assert.Single(cut.FindAll("[data-testid='setup-production-strategy-mismatch']"));
    }

    [Fact]
    public async Task BlueGreenOnAFileThatReadsTheColourPorts_ShowsNoWarning()
    {
        var cut = await OpenSummaryAsync(BlueGreenAnalysis(), strategies: ["bluegreen"]);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='setup-production-strategy-mismatch']")));
    }

    [Fact]
    public async Task AStrategyTheComposeFileFollows_ShowsNoWarning()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"), strategies: ["bluegreen", "replace"]);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='setup-production-strategy-mismatch']")));
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
        Assert.False(variables.ContainsKey("DEPLOY_HAS_API"));
        var parameter = (Dictionary<object, object>)((List<object>)deploy["parameters"]).Single();
        Assert.Equal("deployServer", parameter["name"]);
        Assert.Equal("vps", parameter["default"]);
        // The QA pipeline deploys its disposable stack from the same Compose file (R-19); no other one reads it.
        var qa = (Dictionary<object, object>)Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-qa").YamlDefinition)["variables"];
        Assert.Equal("deploy/compose/remote.compose.yml", qa["COMPOSE_FILE"]);
        Assert.All(Created().Where(pipeline => pipeline.Name is not ("atlas-deploy-prod" or "atlas-qa")),
            pipeline => Assert.DoesNotContain("COMPOSE_FILE", pipeline.YamlDefinition, StringComparison.Ordinal));
    }

    /// <summary>R-11: the Full-stack tile (a monolith) picks the monolith template, never a switch read from the Compose file; its
    /// strategy is the replacement only, whatever the file reads, and nothing about an API is written.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheFullStackTile_PicksTheMonolithTemplate_WithTheReplacementOnly(bool blueGreenFile)
    {
        var analysis = blueGreenFile ? BlueGreenAnalysis() : ReadableAnalysis("vps") with { DeclaresBackPort = false };
        var cut = await OpenSummaryAsync(analysis, sameProject: true);

        Assert.Empty(cut.FindAll("#setup-production-strategy"));
        // The localizer stub of these tests returns the key of the replacement.
        Assert.Equal("PipelineSetupProdReplace", cut.Find("[data-testid='setup-production-strategy-fixed']").TextContent);
        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.Equal("replace", AppliedRequest().Strategy);
        // The setup service decides the API's virtual hosts from the shape chosen, so the request names it.
        Assert.True(AppliedRequest().Monolith);
        var deploy = Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition;
        var parsed = Parse(deploy);
        Assert.Equal("application-deploy-prod@1", parsed["extends"]);
        var variables = (Dictionary<object, object>)parsed["variables"];
        Assert.Equal("replace", variables["DEPLOY_STRATEGY"]);
        Assert.False(variables.ContainsKey("DEPLOY_HAS_API"));
    }

    /// <summary>R-11: the other tile (a front and an API) picks the front-and-API template.</summary>
    [Fact]
    public async Task TheFrontAndBackTile_PicksTheFrontAndApiTemplate_AndKeepsTheStrategyChoice()
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"));

        Assert.NotEmpty(cut.FindAll("#setup-production-strategy"));
        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        var deploy = Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition);
        Assert.Equal("application-deploy-prod-front-back@1", deploy["extends"]);
        Assert.False(((Dictionary<object, object>)deploy["variables"]).ContainsKey("DEPLOY_HAS_API"));
        Assert.False(AppliedRequest().Monolith);
    }

    /// <summary>R-11: the Compose file only checks the shape chosen: when it disagrees, the panel says so and the creation goes
    /// on with the template of the shape the user chose.</summary>
    [Theory]
    [InlineData(true, true, true, "application-deploy-prod")]
    [InlineData(true, false, false, "application-deploy-prod")]
    [InlineData(false, false, true, "application-deploy-prod-front-back")]
    [InlineData(false, true, false, "application-deploy-prod-front-back")]
    public async Task TheComposeFile_ChecksTheShapeChosen_ByAWarningThatNeverBlocks(
        bool fullStack, bool publishesBackPort, bool warned, string template)
    {
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps") with { DeclaresBackPort = publishesBackPort }, sameProject: fullStack);

        Assert.Equal(warned ? 1 : 0, cut.FindAll("[data-testid='setup-production-shape-mismatch']").Count);
        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.Equal($"{template}@1", Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition)["extends"]);
    }

    [Fact]
    public async Task WithSeveralServers_NoneIsGuessed_AndTheCreationWaitsForTheChoice()
    {
        // R-36: the candidate starts the deployment (the default of the wizard) and gives it no server, so the choice is
        // required before the creation instead of leaving a pipeline the chain would refuse. The same case with the chain
        // unticked (the run asks for the server) is PipelineSetupDeployServerRequiredTests.
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps-a", "vps-b"));

        Assert.Null(cut.FindComponents<OmniDropDown<string>>().Single(item => item.Instance.Id == "setup-production-server").Instance.Value);
        Assert.True(cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled"));
        Assert.Empty(Created());
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

    /// <summary>Recette R-80, W6: the repository held no commit yet when the wizard ran, so the analysis read nothing.
    /// The production environment's server does not come from the Compose file, so it is still offered, with the
    /// environment's server as default, and the strategy the user chose is written all the same.</summary>
    [Fact]
    public async Task WhenTheRepositoryCannotBeRead_TheServerOfTheProductionEnvironmentIsStillWrittenIntoThePipeline()
    {
        var cut = await OpenSummaryAsync(new PipelineProductionAnalysisDto
        {
            Readable = false,
            Servers = [new ProductionServerDto { Id = 3, Name = "vps2577917" }],
            DefaultServer = "vps2577917"
        });

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.DoesNotContain(Posts(), post => post.Url.EndsWith(Apply, StringComparison.OrdinalIgnoreCase));
        var deploy = Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition);
        var parameter = (Dictionary<object, object>)((List<object>)deploy["parameters"]).Single();
        Assert.Equal("deployServer", parameter["name"]);
        Assert.Equal("vps2577917", parameter["default"]);
        var variables = (Dictionary<object, object>)deploy["variables"];
        // Nothing of the Compose file is claimed: it was not read.
        Assert.False(variables.ContainsKey("COMPOSE_FILE"));
        Assert.False(variables.ContainsKey("DEPLOY_HAS_API"));
    }

    private static PipelineProductionAnalysisDto CandidateAnalysis(params string[] servers) => ReadableAnalysis(servers) with
    {
        Servers = [.. servers.Select((name, index) => new ProductionServerDto { Id = index + 1, Name = name, Candidate = true })]
    };

    /// <summary>Recette R-77: a project with no server is offered the one the user administers; the summary says it will be
    /// attached, and the deployment is written for it, ports included.</summary>
    [Fact]
    public async Task AProjectWithNoServer_IsGivenTheOnlyServerTheUserAdministers()
    {
        var cut = await OpenSummaryAsync(CandidateAnalysis("vpssim"));

        cut.WaitForAssertion(() => Assert.Equal("vpssim", cut.Find("[data-testid='setup-production-server-name']").TextContent));
        Assert.Contains(cut.FindComponents<PipelineInfoTip>(), tip => tip.Instance.Text == "PipelineSetupProdServerCandidateHelp");
        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.Equal("vpssim", AppliedRequest().Server);
        var deploy = Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition);
        var parameter = (Dictionary<object, object>)((List<object>)deploy["parameters"]).Single();
        Assert.Equal("vpssim", parameter["default"]);
    }

    /// <summary>Recette R-77: among several servers offered, only the one chosen becomes the project's; the others are never
    /// written into the pipeline.</summary>
    [Fact]
    public async Task AmongSeveralServersOffered_OnlyTheOneChosen_IsWrittenIntoThePipeline()
    {
        var cut = await OpenSummaryAsync(CandidateAnalysis("vps-a", "vps-b"));
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = "deploy/compose/remote.compose.yml",
            Server = "vps-b",
            Strategy = "replace",
            DeclaresApi = true,
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "Atlas configuration", Slug = "atlas-config" }
        });
        var servers = cut.FindComponents<OmniDropDown<string>>().Single(item => item.Instance.Id == "setup-production-server");
        await cut.InvokeAsync(() => servers.Instance.ValueChanged.InvokeAsync("vps-b"));

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.Equal("vps-b", AppliedRequest().Server);
        var deploy = Parse(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition);
        var parameter = (Dictionary<object, object>)((List<object>)deploy["parameters"]).Single();
        Assert.Equal("vps-b", parameter["default"]);
        Assert.DoesNotContain("vps-a", Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition,
            StringComparison.Ordinal);
    }

    /// <summary>A server offered but never attached (the repository could not be read, so nothing was applied) is not the
    /// project's: the pipeline never names it.</summary>
    [Fact]
    public async Task WhenNothingIsApplied_AServerOfferedIsNeverWrittenIntoThePipeline()
    {
        var cut = await OpenSummaryAsync(new PipelineProductionAnalysisDto
        {
            Readable = false,
            Servers = [new ProductionServerDto { Id = 4, Name = "vpssim", Candidate = true }],
            DefaultServer = "vpssim"
        });

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.DoesNotContain("vpssim", Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition,
            StringComparison.Ordinal);
    }

    /// <summary>Recette R-80: the wizard no longer ends in silence when it could prepare nothing for production.</summary>
    [Fact]
    public async Task WhenTheRepositoryCannotBeRead_TheEndOfTheWizardSaysProductionIsNotPrepared()
    {
        var cut = await OpenSummaryAsync(new PipelineProductionAnalysisDto { Readable = false });

        await FinishAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.Contains(Services.Toasts(), toast =>
            toast.Severity == OmniSeverity.Warning && toast.Summary == "ProductionNotPrepared");
    }

    /// <summary>Recette R-80: the analysis of the repository takes a moment; a creation asked meanwhile used to find
    /// the setup "not ready" and write nothing. It now waits for the reading, then prepares what it found.</summary>
    [Fact]
    public async Task FinishingBeforeTheRepositoryIsRead_WaitsForItsReading_ThenPreparesProduction()
    {
        var gate = new TaskCompletionSource<PipelineProductionAnalysisDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = await OpenSummaryAsync(ReadableAnalysis("vps"), gate);

        var finishing = FinishAsync(cut);
        await Task.Delay(50, Xunit.TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Posts(), post => post.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase));
        gate.SetResult(ReadableAnalysis("vps"));
        await finishing;

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.Contains(Posts(), post => post.Url.EndsWith(Apply, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("COMPOSE_FILE: \"deploy/compose/remote.compose.yml\"",
            Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition, StringComparison.Ordinal);
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

    /// <summary>Only the production deployment and the QA read the Compose file (recette R-75: the QA deploys its disposable
    /// stack from it), so with neither of them ticked nothing is read from the repository.</summary>
    [Fact]
    public async Task WithoutProductionNorQa_NothingIsReadFromTheRepository()
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
        await cut.Find("[data-testid='setup-option-qa']").ClickAsync(new MouseEventArgs());

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
            new PipelineSetupProductionInput("deploy/compose/remote.compose.yml", "bluegreen", "vps \"eu\""));

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
            "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back"
        }.Select(name => new PipelineTemplateSummaryDto { Id = name.GetHashCode(StringComparison.Ordinal), Name = name, Version = 1 })
    ];
}
