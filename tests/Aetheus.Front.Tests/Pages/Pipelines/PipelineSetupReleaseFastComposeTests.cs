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

/// <summary>Recette R-37 (found by a real run, 2026-11-07): with only the fast release ticked and the production deployment not,
/// the wizard prepared nothing, so the fast release was written with no <c>COMPOSE_FILE</c>, no <c>DEPLOY_STRATEGY</c> and no
/// <c>deployServer</c>, and fell back on a Compose file at the root of the repository. It deploys to the production server, so
/// it is given what the production setup reads, whether or not the production pipeline itself is created.</summary>
public sealed class PipelineSetupReleaseFastComposeTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";
    private const string ComposeFile = "deploy/compose/remote.compose.yml";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupReleaseFastComposeTests() => _handler = BunitTestHelper.RegisterServices(this);

    private Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync(PipelineProductionAnalysisDto analysis, params string[] toggled) =>
        OpenSummaryAsync(analysis, new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" }, toggled);

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync(
        PipelineProductionAnalysisDto analysis, PipelineSetupReadinessDto readiness, params string[] toggled)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness", readiness);
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, analysis);
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = ComposeFile,
            Server = "vps",
            Strategy = "replace",
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "Atlas configuration", Slug = "atlas-config" }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        foreach (var option in toggled)
            await cut.Find($"[data-testid='setup-option-{option}']").ClickAsync(new MouseEventArgs());
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private async Task<CreatePipelineRequest[]> FinishAsync(IRenderedComponent<PipelineSetupWizard> cut, int expected)
    {
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

    private static Dictionary<object, object> Definition(CreatePipelineRequest request) =>
        Yaml.Deserialize<Dictionary<object, object>>(request.YamlDefinition);

    private static PipelineProductionAnalysisDto Readable(string? composeFile) => new()
    {
        Readable = true,
        Branch = "develop",
        ComposeFiles = composeFile is null ? [] : [composeFile],
        ComposeFile = composeFile,
        Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
        DefaultServer = "vps"
    };

    // QA ticked: the QA path already gave the Compose file to the fast release; the strategy and the server were still missing.
    // QA unticked: the case of the real run, where the fast release got nothing at all.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FastReleaseTickedWithoutProduction_TheFastReleaseGetsTheComposeFileTheStrategyAndTheServer(bool qa)
    {
        var cut = await OpenSummaryAsync(Readable(ComposeFile), qa ? ["production", "fast-deploy"] : ["production", "qa", "fast-deploy"]);

        var created = await FinishAsync(cut, qa ? 7 : 6);

        var fast = Assert.Single(created, request => request.Name == "atlas-release-fast");
        var variables = (Dictionary<object, object>)Definition(fast)["variables"];
        Assert.Equal(ComposeFile, variables["COMPOSE_FILE"]);
        Assert.Equal("replace", variables["DEPLOY_STRATEGY"]);
        var parameter = (Dictionary<object, object>)Assert.Single((List<object>)Definition(fast)["parameters"]);
        Assert.Equal("deployServer", parameter["name"]);
        Assert.Equal("vps", parameter["default"]);
        // The production pipeline is not created, and the other pipelines carry nothing of the deployment.
        Assert.DoesNotContain(created, request => request.Name == "atlas-deploy-prod");
        Assert.All(created.Where(request => request.Name != "atlas-release-fast" && request.Name != "atlas-qa"),
            request => Assert.DoesNotContain("COMPOSE_FILE", request.YamlDefinition, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FastReleaseTickedWhenNoComposeFileIsRead_NeitherTheFileNorTheStrategyIsClaimed_ButTheServerIs()
    {
        var cut = await OpenSummaryAsync(Readable(null), "production", "fast-deploy");

        var created = await FinishAsync(cut, 7);

        var fast = Assert.Single(created, request => request.Name == "atlas-release-fast");
        Assert.DoesNotContain("COMPOSE_FILE", fast.YamlDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("DEPLOY_STRATEGY", fast.YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("deployServer", fast.YamlDefinition, StringComparison.Ordinal);
    }

    private static PipelineSetupReadinessDto VaultMissing() => new()
    {
        RepositoryInspected = true,
        InspectedBranch = "develop",
        Checks =
        [
            new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.MissingRequiredVaults,
                Severity = PipelineSetupReadinessSeverity.Blocking,
                Items = ["atlas.prod"]
            }
        ]
    };

    private string[] PostBodies(string urlEnd) => [.. _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method && request.Url.EndsWith(urlEnd, StringComparison.OrdinalIgnoreCase))
        .Select(request => request.Body ?? string.Empty)];

    /// <summary>R-37: the fast release alone prepares the production setup, which writes into the vault <c>&lt;slug&gt;.prod</c>, but only
    /// the production deployment template declares that vault. The summary reads that template too, so the vault is in its list of
    /// what is to create, and the creation of the libraries and vaults reads the same list.</summary>
    [Fact]
    public async Task FastReleaseAlone_TheSummaryListsTheProductionVault_AndCreatesItWithTheSameTemplates()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/provision", new PipelineRequirementsProvisionResultDto
        {
            Vaults = [new ProvisionedRequirementDto { Id = 13, Name = "atlas.prod", Keys = [] }]
        });
        var cut = await OpenSummaryAsync(Readable(ComposeFile), VaultMissing(), "production", "qa", "fast-deploy");

        cut.WaitForAssertion(() => Assert.Contains("atlas.prod", cut.Find("[data-testid='setup-provisioned-pending']").TextContent, StringComparison.Ordinal));
        // The check the summary shows is the last one made: an earlier one may predate the options chosen.
        var lastCheck = PostBodies("api/pipelines/setup/readiness")[^1];
        Assert.Contains("application-release-fast", lastCheck, StringComparison.Ordinal);
        Assert.Contains("application-deploy-prod-front-back", lastCheck, StringComparison.Ordinal);

        await cut.Find("[data-testid='setup-provision'] button").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Single(PostBodies("api/pipelines/setup/provision")));
        Assert.Contains("application-deploy-prod-front-back", PostBodies("api/pipelines/setup/provision")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheProductionTemplateIsAddedToTheReadiness_OnlyForAFastReleaseWithoutTheProductionDeployment()
    {
        string[] fast = ["application-ci", "application-release-fast"];
        Assert.Equal([.. fast, "application-deploy-prod-front-back"], PipelineSetupRoles.ReadinessTemplates(fast, fullStack: false));
        Assert.Equal([.. fast, "application-deploy-prod"], PipelineSetupRoles.ReadinessTemplates(fast, fullStack: true));
        string[] both = [.. fast, "application-deploy-prod"];
        Assert.Equal(both, PipelineSetupRoles.ReadinessTemplates(both, fullStack: true));
        string[] none = ["application-ci", "application-qa"];
        Assert.Equal(none, PipelineSetupRoles.ReadinessTemplates(none, fullStack: false));
    }

    /// <summary>R-37: with the fast release alone, the production setup is shown, so the server is chosen there as when the
    /// production deployment is ticked; the one chosen is applied and becomes the default of the fast release's
    /// <c>deployServer</c>.</summary>
    [Fact]
    public async Task FastReleaseAlone_TheServerIsChosenOnTheSummary_AndWrittenAsTheDefaultOfDeployServer()
    {
        var analysis = new PipelineProductionAnalysisDto
        {
            Readable = true,
            Branch = "develop",
            ComposeFiles = [ComposeFile],
            ComposeFile = ComposeFile,
            Servers = [new ProductionServerDto { Id = 1, Name = "vps-a" }, new ProductionServerDto { Id = 2, Name = "vps-b" }]
        };
        var cut = await OpenSummaryAsync(analysis, "production", "qa", "fast-deploy");
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = ComposeFile,
            Server = "vps-b",
            Strategy = "replace",
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "Atlas configuration", Slug = "atlas-config" }
        });

        var servers = cut.FindComponents<OmniDropDown<string>>().Single(item => item.Instance.Id == "setup-production-server");
        await cut.InvokeAsync(() => servers.Instance.ValueChanged.InvokeAsync("vps-b"));
        var created = await FinishAsync(cut, 6);

        Assert.Contains("\"server\":\"vps-b\"", PostBodies(Apply)[0], StringComparison.Ordinal);
        var fast = Assert.Single(created, request => request.Name == "atlas-release-fast");
        var parameter = (Dictionary<object, object>)Assert.Single((List<object>)Definition(fast)["parameters"]);
        Assert.Equal("vps-b", parameter["default"]);
    }

    /// <summary>R-37: a refusal of the production setup (here the vault the setup writes into does not exist) is said with the
    /// server's own sentence, never with the bare "Operation failed"; nothing is created.</summary>
    [Fact]
    public async Task ARefusalOfTheProductionSetup_IsShownWithTheServersReason()
    {
        const string reason = "The vault 'atlas.prod' does not exist: create the libraries and vaults first.";
        var cut = await OpenSummaryAsync(Readable(ComposeFile), "production", "qa", "fast-deploy");
        _handler.SetRawResponse(HttpMethod.Post, Apply, $"{{\"message\":\"{reason}\"}}", "application/json", HttpStatusCode.BadRequest);

        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast =>
            toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Danger
            && (toast.Summary + toast.Detail).Contains(reason, StringComparison.Ordinal)));
        Assert.DoesNotContain("OperationFailed", Services.Toasts().Select(toast => toast.Summary));
        Assert.Empty(Created());
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back",
            "application-release-fast"
        }.Select(name => new PipelineTemplateSummaryDto { Id = name.GetHashCode(StringComparison.Ordinal), Name = name, Version = 1 })
    ];
}
