// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-36 (run 3119): the candidate starts the deployment and gives it no server, so with the chain on the summary
/// requires the production server before the creation and writes it as the default of <c>deployServer</c>; a candidate created in a
/// second pass is chained to the deployment pipeline the project already holds.</summary>
public sealed class PipelineSetupDeployServerRequiredTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";
    private const string Required = "[data-testid='setup-production-server-required']";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupDeployServerRequiredTests() => _handler = BunitTestHelper.RegisterServices(this);

    /// <summary>Two servers and no production environment: the backend proposes none (nothing is guessed).</summary>
    private static PipelineProductionAnalysisDto TwoServers() => new()
    {
        Readable = true,
        Branch = "develop",
        ComposeFiles = ["deploy/compose/remote.compose.yml"],
        ComposeFile = "deploy/compose/remote.compose.yml",
        Servers = [new ProductionServerDto { Id = 1, Name = "vps-a" }, new ProductionServerDto { Id = 2, Name = "vps-b" }],
        DefaultServer = null,
        Variables = [new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port }]
    };

    /// <summary>The wizard on its summary, reached through its steps (production and the candidate ticked by default, so the
    /// chain is on) after the deployment option was unticked when <paramref name="untickChain"/>.</summary>
    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync(PipelineProductionAnalysisDto analysis, bool untickChain = false)
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
            Server = "vps-b",
            Strategy = "replace",
            DeclaresApi = true,
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "Atlas configuration", Slug = "atlas-config" }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        if (untickChain)
            await cut.Find("[data-testid='setup-option-deploy-after-candidate']").ClickAsync(new MouseEventArgs());
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back"
        }.Select(name => new PipelineTemplateSummaryDto { Id = name.GetHashCode(StringComparison.Ordinal), Name = name, Version = 1 })
    ];

    private static AngleSharp.Dom.IElement FinishButton(IRenderedComponent<PipelineSetupWizard> cut) =>
        cut.Find(".omni-wizard__actions button.omni-wizard__finish");

    private CreatePipelineRequest[] Created() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

    private static string DeployDefaultServer(string yaml)
    {
        var parameters = (List<object>)Yaml.Deserialize<Dictionary<object, object>>(yaml)["parameters"];
        return (string)((Dictionary<object, object>)parameters.Single())["default"];
    }

    [Fact]
    public async Task ChainOn_SeveralServersAndNoDefault_AsksForTheServer_AndHoldsTheCreation()
    {
        var cut = await OpenSummaryAsync(TwoServers());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Required)));
        // The standard required mark, and the reason shown on the field.
        Assert.Equal("*", cut.Find($"{Required} .omni-label__required").TextContent);
        Assert.Contains("PipelineSetupProdServerRequiredError", cut.Find(Required).TextContent, StringComparison.Ordinal);
        Assert.True(FinishButton(cut).HasAttribute("disabled"));
        Assert.Empty(Created());
    }

    [Fact]
    public async Task ChainOn_ChoosingTheServer_ReleasesTheCreation_AndWritesItAsTheDefault()
    {
        var cut = await OpenSummaryAsync(TwoServers());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Required)));
        var servers = cut.FindComponents<OmniDropDown<string>>().Single(item => item.Instance.Id == "setup-production-server");

        await cut.InvokeAsync(() => servers.Instance.ValueChanged.InvokeAsync("vps-b"));
        cut.WaitForAssertion(() => Assert.False(FinishButton(cut).HasAttribute("disabled")));
        Assert.DoesNotContain("PipelineSetupProdServerRequiredError", cut.Find(Required).TextContent, StringComparison.Ordinal);
        await FinishButton(cut).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        Assert.Equal("vps-b", DeployDefaultServer(Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition));
    }

    [Fact]
    public async Task ChainUnticked_TheServerIsNotRequired_AndTheCreationIsNotHeld()
    {
        var cut = await OpenSummaryAsync(TwoServers(), untickChain: true);

        // The analysis is read once the summary shows: the creation is released when it is in, with no server to choose.
        cut.WaitForAssertion(() => Assert.False(FinishButton(cut).HasAttribute("disabled")));
        Assert.Empty(cut.FindAll(Required));
        // No server was chosen, so the preparation names none (the backend answers with the server the request gave).
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = "deploy/compose/remote.compose.yml",
            Server = string.Empty,
            Strategy = "replace",
            DeclaresApi = true,
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "Atlas configuration", Slug = "atlas-config" }
        });
        await FinishButton(cut).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        // Nothing starts the deployment by itself: none is guessed, and the run asks for one among the project's servers.
        var deploy = Yaml.Deserialize<Dictionary<object, object>>(
            Assert.Single(Created(), pipeline => pipeline.Name == "atlas-deploy-prod").YamlDefinition);
        var parameter = (Dictionary<object, object>)((List<object>)deploy["parameters"]).Single();
        Assert.Equal("deployServer", parameter["name"]);
        Assert.False(parameter.ContainsKey("default"));
        Assert.Equal(["vps-a", "vps-b"], ((List<object>)parameter["allowed_values"]).Cast<string>());
    }

    private static PipelineSetupYamlInput SecondPass(IReadOnlySet<string>? existing, bool chain = true) => new(
        "atlas", (true, true, false), "package-web-analytics-dotnet",
        Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false,
        ["candidate"], RunDeployAfterCandidate: chain, ExistingPipelines: existing);

    [Fact]
    public void ACandidateCreatedInASecondPass_IsChainedToTheDeployProdTheProjectAlreadyHolds()
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3,
            SecondPass(new HashSet<string> { "atlas-deploy-prod" }));

        Assert.Contains("on_success:", yaml, StringComparison.Ordinal);
        Assert.Contains("pipeline: atlas-deploy-prod", yaml, StringComparison.Ordinal);
        Assert.Contains("release: latest", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ACandidateCreatedInASecondPass_IsNotChained_WhenTheProjectHasNoDeployProd_OrTheChainIsOff()
    {
        Assert.DoesNotContain("on_success", PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3,
            SecondPass(new HashSet<string> { "atlas-ci" })), StringComparison.Ordinal);
        Assert.DoesNotContain("on_success", PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3,
            SecondPass(new HashSet<string> { "atlas-deploy-prod" }, chain: false)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheChainOption_IsOffered_ForACandidateWhoseDeployProdIsHeldByTheProject()
    {
        Assert.False(PipelineSetupDeployChain.IsOffered(["candidate"]));
        Assert.True(PipelineSetupDeployChain.IsOffered(["candidate"], deployProdHeld: true));
        Assert.False(PipelineSetupDeployChain.IsOffered(["ci"], deployProdHeld: true));
    }
}
