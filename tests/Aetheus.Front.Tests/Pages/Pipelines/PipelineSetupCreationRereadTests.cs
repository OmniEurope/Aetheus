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

/// <summary>Recette R-6 and R-7 (W8): the code reached the repository after the wizard read it (the user pushed from a
/// terminal while the summary was open, without going through the first-push offer). The reading made when the summary
/// opened found no <c>develop</c> and no Compose file, and used to stand until the pipelines were written, so they came out
/// without <c>source_branch:</c>, <c>COMPOSE_FILE</c> and <c>DEPLOY_HAS_API</c> and an agent had to add them by hand. The
/// creation looks again at what it found missing, so a push made meanwhile is written the first time.</summary>
public sealed class PipelineSetupCreationRereadTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";
    private const string ComposePath = "deploy/compose/remote.compose.yml";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupCreationRereadTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineProductionAnalysisDto EmptyRepository() => new() { Readable = false };

    private static PipelineProductionAnalysisDto WithCompose() => new()
    {
        Readable = true,
        Branch = "main",
        ComposeFiles = [ComposePath],
        ComposeFile = ComposePath,
        Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
        DefaultServer = "vps",
        DeclaresBackPort = true,
        Variables = [new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port }]
    };

    private void ListBranches(params string[] names) =>
        _handler.SetPaginatedJsonResponse("api/git/repos/5/branches",
            names.Select(name => new GitLightBranchDto { Name = name, IsDefault = name == "main" }));

    private void Arrange(PipelineProductionAnalysisDto analysis, params string[] branches)
    {
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "main", InternalRepositoryId = 5 }]);
        ListBranches(branches);
        _handler.SetJsonResponse("api/git/repos/5", new GitLightRepoDto { Id = 5, DefaultBranch = "main" });
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "main" });
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, analysis);
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

    private IEnumerable<CreatePipelineRequest> CreatedPipelines() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!, Web)!);

    private async Task FinishAsync(IRenderedComponent<PipelineSetupWizard> cut)
    {
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(7, CreatedPipelines().Count()));
    }

    [Fact]
    public async Task ADevelopBranchPushedAfterTheSummaryOpened_IsWrittenAsSourceBranchOfEveryPipeline()
    {
        Arrange(WithCompose(), "main");
        var cut = await OpenSummaryAsync();
        ListBranches("main", "develop");

        await FinishAsync(cut);

        Assert.All(CreatedPipelines(), request =>
        {
            Assert.Equal("develop", request.SourceBranch);
            Assert.Equal("develop", Yaml.Deserialize<Dictionary<object, object>>(request.YamlDefinition)["source_branch"]);
        });
    }

    [Fact]
    public async Task AComposeFilePushedAfterTheSummaryOpened_IsWrittenInTheDeployPipeline_WithItsApi()
    {
        Arrange(EmptyRepository(), "main");
        var cut = await OpenSummaryAsync();
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, WithCompose());

        await FinishAsync(cut);

        var deploy = Yaml.Deserialize<Dictionary<object, object>>(
            Assert.Single(CreatedPipelines(), request => request.Name == "atlas-deploy-prod").YamlDefinition);
        var variables = Assert.IsType<Dictionary<object, object>>(deploy["variables"]);
        Assert.Equal(ComposePath, variables["COMPOSE_FILE"]);
        Assert.False(variables.ContainsKey("DEPLOY_HAS_API"));
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
