// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Shared.Components.Git;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-9 (W8): a repository whose default branch is main and whose work lives on develop. The wizard writes
/// <c>source_branch: develop</c> in every pipeline and reads the Compose file of that same branch, so the production
/// pipeline carries <c>COMPOSE_FILE</c> and <c>DEPLOY_HAS_API</c> without a hand edit. The branch travels in the analysis
/// and in the application of the setup; the reading itself is proved on the server, in
/// <c>PipelineProductionSetupBranchTests</c>.</summary>
public sealed class PipelineSetupProductionBranchTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";
    private const string ComposePath = "deploy/compose/remote.compose.yml";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupProductionBranchTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineProductionAnalysisDto ReadableAnalysis() => new()
    {
        Readable = true,
        Branch = "develop",
        ComposeFiles = [ComposePath],
        ComposeFile = ComposePath,
        Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
        DefaultServer = "vps",
        DeclaresBackPort = true,
        Variables = [new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port }]
    };

    private void Arrange()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "main", InternalRepositoryId = 5 }]);
        _handler.SetPaginatedJsonResponse("api/git/repos/5/branches",
            new[] { "main", "develop" }.Select(name => new GitLightBranchDto { Name = name, IsDefault = name == "main" }));
        _handler.SetJsonResponse("api/git/repos/5", new GitLightRepoDto { Id = 5, DefaultBranch = "main" });
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "main" });
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, ReadableAnalysis());
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = ComposePath,
            Server = "vps",
            Strategy = "replace",
            DeclaresApi = true,
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "Atlas configuration", Slug = "atlas-config", Created = true }
        });
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync()
    {
        Arrange();
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

    private IEnumerable<string> PostBodies(string urlEnd) => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith(urlEnd, StringComparison.OrdinalIgnoreCase))
        .Select(request => request.Body!);

    [Fact]
    public async Task TheComposeFile_IsReadOnTheBranchThePipelinesAreCreatedOn_AndTheYamlsCarryWhatWasDetected()
    {
        var cut = await OpenSummaryAsync();

        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(7, PostBodies("api/pipelines").Count()));

        // The reading and the setup are asked for develop, the branch the pipelines are written on, not the default one.
        Assert.All(PostBodies(Analysis), body => Assert.Equal("develop", JsonSerializer.Deserialize<PipelineProductionAnalysisRequest>(body, Web)!.Branch));
        Assert.Equal("develop", JsonSerializer.Deserialize<PipelineProductionApplyRequest>(Assert.Single(PostBodies(Apply)), Web)!.Branch);

        var created = PostBodies("api/pipelines").Select(body => JsonSerializer.Deserialize<CreatePipelineRequest>(body, Web)!).ToArray();
        Assert.All(created, request =>
        {
            Assert.Equal("develop", request.SourceBranch);
            Assert.Equal("develop", Yaml.Deserialize<Dictionary<object, object>>(request.YamlDefinition)["source_branch"]);
        });
        var deploy = Yaml.Deserialize<Dictionary<object, object>>(Assert.Single(created, request => request.Name == "atlas-deploy-prod").YamlDefinition);
        var variables = Assert.IsType<Dictionary<object, object>>(deploy["variables"]);
        Assert.Equal(ComposePath, variables["COMPOSE_FILE"]);
        Assert.False(variables.ContainsKey("DEPLOY_HAS_API"));
        Assert.Equal("replace", variables["DEPLOY_STRATEGY"]);
    }

    [Fact]
    public async Task TheReading_IsMadeAgain_WhenTheBranchChosenChanged_AndOnlyThen()
    {
        Arrange();
        var branch = "main";
        var production = new PipelineSetupProduction(Services.GetRequiredService<ApiClient>(), () => { }, () => branch);

        await production.LoadAsync(7);
        await production.LoadAsync(7);
        Assert.Single(PostBodies(Analysis));

        branch = "develop";
        await production.LoadAsync(7);
        var bodies = PostBodies(Analysis).Select(body => JsonSerializer.Deserialize<PipelineProductionAnalysisRequest>(body, Web)!).ToArray();
        Assert.Equal(["main", "develop"], bodies.Select(body => body.Branch));
        // The Compose file chosen before is asked again on the new branch (the analysis falls back on its own proposal there).
        Assert.Equal(ComposePath, bodies[1].ComposeFile);

        await production.LoadAsync(7);
        Assert.Equal(2, PostBodies(Analysis).Count());
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
