// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-38 (project oe1): a production pipeline created while the Compose file could not be read has no configuration
/// repository, no Apache file, no variable and no port. On its page, one action completes that preparation with the same setup
/// endpoints as the wizard, instead of asking to delete the pipeline and run the wizard again.</summary>
public sealed class PipelineProductionCompletionTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";
    private const string Root = "[data-testid='pipeline-production-completion']";
    private const string Action = "[data-testid='pipeline-production-complete']";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineProductionCompletionTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineDto Production(string template = "application-deploy-prod-front-back@1") => new()
    {
        Id = 190,
        Name = "oe1-deploy-prod",
        ProjectId = 21,
        SourceBranch = "develop",
        YamlDefinition = $"name: oe1-deploy-prod\nextends: {template}\ntrigger: manual\nvariables:\n  APPLICATION_PREFIX: \"oe1\"\nstages: []"
    };

    /// <summary>What the repository gave when the wizard ran: nothing to read.</summary>
    private static PipelineProductionAnalysisDto NothingRead() => new()
    {
        Readable = true,
        Branch = "develop",
        Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
        DefaultServer = "vps"
    };

    private static PipelineProductionAnalysisDto ComposeNowThere(ProjectConfigRepositoryDto? configuration = null) => new()
    {
        Readable = true,
        Branch = "develop",
        ComposeFiles = ["deploy/compose/remote.compose.yml"],
        ComposeFile = "deploy/compose/remote.compose.yml",
        Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
        DefaultServer = "vps",
        ConfigRepository = configuration,
        Variables = [new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port }]
    };

    private IRenderedComponent<PipelineProductionCompletion> Open(PipelineDto pipeline, PipelineProductionAnalysisDto analysis)
    {
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, analysis);
        var cut = Render<PipelineProductionCompletion>(parameters => parameters.Add(item => item.Pipeline, pipeline));
        cut.WaitForState(() => _handler.RequestDetails.Any(request => request.Url.EndsWith(Analysis, StringComparison.OrdinalIgnoreCase)));
        return cut;
    }

    private IEnumerable<(string Method, string Url, string? Body)> Calls(string urlEnd) => _handler.RequestDetails
        .Where(request => request.Url.EndsWith(urlEnd, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void AProductionPipelineWhoseComposeFileWasNeverRead_OffersToCompleteThePreparation_AndSaysWhatIsMissing()
    {
        var cut = Open(Production(), NothingRead());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Root)));
        Assert.Equal("PipelineProdCompletionNoCompose", cut.Find("[data-testid='pipeline-production-completion-cause']").TextContent);
        Assert.NotEmpty(cut.FindAll("#pipeline-production-compose"));
        Assert.Single(cut.FindAll(Action));
        Assert.Equal("PipelineProdCompletionAction", cut.Find(Action).TextContent.Trim());
        Assert.DoesNotContain("delete", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARepositoryThatCannotBeRead_SaysSo()
    {
        _handler.SetJsonResponse("api/projects/21", new ProjectDetailDto { Id = 21, Name = "oe1" });
        var cut = Open(Production(), new PipelineProductionAnalysisDto { Readable = false, Servers = [] });

        cut.WaitForAssertion(() => Assert.Equal("PipelineProdCompletionUnreadable",
            cut.Find("[data-testid='pipeline-production-completion-cause']").TextContent));
    }

    [Fact]
    public void AnAlreadyPreparedProduction_ShowsNothing()
    {
        var prepared = ComposeNowThere(new ProjectConfigRepositoryDto { Id = 9, Name = "oe1 configuration", Slug = "oe1-config" }) with
        {
            Variables = [new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port, Exists = true }]
        };

        var cut = Open(Production(), prepared);

        cut.WaitForState(() => _handler.RequestDetails.Count > 0);
        Assert.Empty(cut.FindAll(Root));
        Assert.Empty(cut.FindAll("[data-testid='pipeline-production-completed']"));
    }

    /// <summary>R-38: the configuration repository exists and the ports are all reserved, but the Apache templates the deployment
    /// applies are held nowhere: the completion is offered, since only the preparation adds them.</summary>
    [Fact]
    public void AConfigurationRepositoryLackingTheApacheTemplates_OffersToCompleteThePreparation()
    {
        var prepared = ComposeNowThere(new ProjectConfigRepositoryDto { Id = 9, Name = "oe1 configuration", Slug = "oe1-config" }) with
        {
            Variables = [new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port, Exists = true }],
            MissingApacheTemplates = [".pipeline/configs/apache/app-http.conf", ".pipeline/configs/apache/api-http.conf"]
        };

        var cut = Open(Production(), prepared);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Root)));
        Assert.Equal("PipelineProdCompletionIncomplete", cut.Find("[data-testid='pipeline-production-completion-cause']").TextContent);
        Assert.Single(cut.FindAll(Action));
    }

    /// <summary>A monolith has no API virtual host: only the API's templates missing is not a preparation to complete.</summary>
    [Fact]
    public void AMonolithMissingOnlyTheApiTemplates_ShowsNothing()
    {
        var prepared = ComposeNowThere(new ProjectConfigRepositoryDto { Id = 9, Name = "oe1 configuration", Slug = "oe1-config" }) with
        {
            Variables = [new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port, Exists = true }],
            MissingApacheTemplates = [".pipeline/configs/apache/api-http.conf", ".pipeline/configs/apache/api-https.conf"]
        };

        var cut = Open(Production("application-deploy-prod@1"), prepared);

        cut.WaitForState(() => _handler.RequestDetails.Count > 0);
        Assert.Empty(cut.FindAll(Root));
    }

    [Fact]
    public void APipelineThatIsNotTheProductionDeployment_ShowsNothing_AndReadsNothing()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, NothingRead());
        var cut = Render<PipelineProductionCompletion>(parameters => parameters.Add(item => item.Pipeline, Production("application-ci@1")));

        Assert.Empty(cut.FindAll(Root));
        Assert.Empty(Calls(Analysis));
    }

    [Fact]
    public async Task R12_TheDefinitionUpdate_KeepsTheSourceRepositoryThePipelineIsBoundTo()
    {
        var cut = Open(Production() with { SourceRepositoryId = 33 }, ComposeNowThere());
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = "deploy/compose/remote.compose.yml",
            Server = "vps",
            Strategy = "replace",
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "oe1 configuration", Slug = "oe1-config", Created = true }
        });
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/190", new PipelineDto { Id = 190, Name = "oe1-deploy-prod" });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Root)));

        await cut.Find(Action).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='pipeline-production-completed']")));
        var update = JsonSerializer.Deserialize<UpdatePipelineRequest>(
            Assert.Single(Calls("api/pipelines/190"), call => call.Method == HttpMethod.Put.Method).Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(33, update.SourceRepositoryId);
    }

    [Fact]
    public async Task TheAction_RunsTheSetupWithTheComposeFileFound_AndAddsItToThePipelineDefinition()
    {
        var cut = Open(Production(), ComposeNowThere());
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = "deploy/compose/remote.compose.yml",
            Server = "vps",
            Strategy = "replace",
            DeclaresApi = true,
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "oe1 configuration", Slug = "oe1-config", Created = true }
        });
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/190", new PipelineDto { Id = 190, Name = "oe1-deploy-prod" });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Root)));

        await cut.Find(Action).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='pipeline-production-completed']")));
        var applied = JsonSerializer.Deserialize<PipelineProductionApplyRequest>(
            Assert.Single(Calls(Apply)).Body!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(21, applied.ProjectId);
        Assert.Equal("deploy/compose/remote.compose.yml", applied.ComposeFile);
        Assert.Equal("vps", applied.Server);
        Assert.Equal("develop", applied.Branch);
        var update = JsonSerializer.Deserialize<UpdatePipelineRequest>(
            Assert.Single(Calls("api/pipelines/190"), call => call.Method == HttpMethod.Put.Method).Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains("COMPOSE_FILE: \"deploy/compose/remote.compose.yml\"", update.YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("DEPLOY_STRATEGY: \"replace\"", update.YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_PREFIX: \"oe1\"", update.YamlDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheComposePathGivenIsNotInTheRepository_ItIsNamedAndNothingIsApplied()
    {
        var cut = Open(Production(), NothingRead());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipeline-production-compose")));
        await cut.Find("#pipeline-production-compose").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "ops/missing.yml" });

        await cut.Find(Action).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal("PipelineProdCompletionComposeNotFound",
            cut.Find("[data-testid='pipeline-production-completion-error']").TextContent));
        // The path typed was the one asked for, on the branch of the pipeline.
        Assert.Contains(Calls(Analysis), call => call.Body!.Contains("ops/missing.yml", StringComparison.Ordinal)
            && call.Body.Contains("develop", StringComparison.Ordinal));
        Assert.Empty(Calls(Apply));
    }

    [Fact]
    public async Task WithNoComposePathAtAll_NothingIsApplied_AndTheClickSaysItWasRetriedAndIsStillBlocked()
    {
        var cut = Open(Production(), NothingRead());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Action)));

        await cut.Find(Action).ClickAsync(new MouseEventArgs());

        // R-27: a click always ends on a visible outcome, even when the failure is the cause already shown above.
        cut.WaitForAssertion(() => Assert.Contains("PipelineProdCompletionStillBlocked",
            cut.Find("[data-testid='pipeline-production-completion-error']").TextContent, StringComparison.Ordinal));
        Assert.Equal("PipelineProdCompletionNoCompose", cut.Find("[data-testid='pipeline-production-completion-cause']").TextContent);
        Assert.Empty(Calls(Apply));
    }

    private const string Oe1Address = "https://github.com/OmniEurope/OmniEurope.Blazor.git";
    private const string MirrorAddress = "api/external-repos/project/21/mirror-address";

    /// <summary>R-27 (project oe1): the project names an external address, has no mirror and no repository of its own: the
    /// analysis reads nothing. The API doubles stand for the project, its (empty) repository list and the mirror call.</summary>
    private IRenderedComponent<PipelineProductionCompletion> OpenUnmirrored(List<GitLightRepoDto>? repositories = null)
    {
        _handler.SetJsonResponse("api/projects/21", new ProjectDetailDto { Id = 21, Name = "oe1", RepositoryUrl = Oe1Address });
        _handler.SetPaginatedJsonResponse("api/git/repos", repositories ?? []);
        return Open(Production(), new PipelineProductionAnalysisDto { Readable = false, Servers = [] });
    }

    [Fact]
    public void AnExternalAddressWithoutMirror_IsSaidAsSuch_NotAsARepositoryWithoutCommit()
    {
        var cut = OpenUnmirrored();

        cut.WaitForAssertion(() => Assert.Equal("PipelineProdCompletionAddressNotMirrored",
            cut.Find("[data-testid='pipeline-production-completion-cause']").TextContent));
    }

    [Fact]
    public async Task CompletingAnUnmirroredExternalAddress_CreatesTheMirrorThroughTheProjectEndpoint_ThenPreparesFromIt()
    {
        var cut = OpenUnmirrored();
        _handler.SetJsonResponse(HttpMethod.Post, MirrorAddress,
            new ExternalRepoDto { ProjectId = 21, IsProjectSource = true, MirrorStatus = GitMirrorStatus.Ready });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Action)));
        // Once the mirror exists, the analysis reads its Compose file.
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, ComposeNowThere());
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = "deploy/compose/remote.compose.yml",
            Strategy = "replace",
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Name = "c", Slug = "c" }
        });
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/190", Production());

        await cut.Find(Action).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Single(Calls(Apply)));
        // The endpoint is called with no body: the address is neither sent nor read from here. The mirror comes before the reading.
        var call = Assert.Single(Calls(MirrorAddress));
        Assert.DoesNotContain("github", call.Body ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var requests = _handler.RequestDetails;
        Assert.True(requests.FindIndex(request => request.Url.EndsWith(MirrorAddress, StringComparison.Ordinal))
            < requests.FindLastIndex(request => request.Url.EndsWith(Analysis, StringComparison.OrdinalIgnoreCase)),
            "the mirror is created before the repository is read again");
    }

    [Fact]
    public async Task ARefusedMirrorCreation_ShowsTheServerMessage_AndPreparesNothing()
    {
        var cut = OpenUnmirrored();
        _handler.SetRawResponse(HttpMethod.Post, MirrorAddress,
            """{"message":"Could not reach the external repository: Remote unreachable."}""", "application/json",
            System.Net.HttpStatusCode.BadRequest);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Action)));

        await cut.Find(Action).ClickAsync(new MouseEventArgs());

        // The test localizer returns the resource key, so the server's sentence is not in the text; the key of the creation failure is.
        cut.WaitForAssertion(() => Assert.Contains("PipelineSetupMirrorCreateFailed",
            cut.Find("[data-testid='pipeline-production-completion-error']").TextContent, StringComparison.Ordinal));
        Assert.Empty(Calls(Apply));
    }

    [Fact]
    public async Task AProjectWithARepositoryOfItsOwn_IsNotGivenAMirrorBecauseOfAStrayAddress()
    {
        var cut = OpenUnmirrored([new GitLightRepoDto { Id = 5, Name = "oe1", ProjectId = 21 }]);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Action)));

        await cut.Find(Action).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains("PipelineProdCompletionStillBlocked",
            cut.Find("[data-testid='pipeline-production-completion-error']").TextContent, StringComparison.Ordinal));
        Assert.Empty(Calls(MirrorAddress));
    }

    [Fact]
    public async Task AnExternalSourceWhoseMirrorIsEmpty_IsSynchronizedBeforeThePreparationReadsItAgain()
    {
        _handler.SetJsonResponse("api/external-repos/project/21", new ExternalRepoDto { ProjectId = 21, MirrorStatus = GitMirrorStatus.Ready });
        _handler.SetJsonResponse(HttpMethod.Post, "api/external-repos/project/21/sync",
            new ExternalRepoDto { ProjectId = 21, MirrorStatus = GitMirrorStatus.Ready });
        var cut = Open(Production(), new PipelineProductionAnalysisDto
        {
            Readable = false,
            ExternalSource = true,
            SourceProblem = ProductionSourceProblem.ExternalNotSynchronized
        });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Action)));

        await cut.Find(Action).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Single(Calls("api/external-repos/project/21/sync")));
        var requests = _handler.RequestDetails;
        Assert.True(requests.FindIndex(request => request.Url.EndsWith("project/21/sync", StringComparison.Ordinal))
            < requests.FindLastIndex(request => request.Url.EndsWith(Analysis, StringComparison.OrdinalIgnoreCase)),
            "the mirror is synchronized before the repository is read again");
    }

    [Fact]
    public async Task AMirrorThatCannotBeSynchronized_SaysSoInsteadOfPreparingFromNothing()
    {
        _handler.SetJsonResponse("api/external-repos/project/21", new ExternalRepoDto { ProjectId = 21, MirrorStatus = GitMirrorStatus.Ready });
        _handler.SetJsonResponse(HttpMethod.Post, "api/external-repos/project/21/sync",
            new ExternalRepoDto { ProjectId = 21, MirrorStatus = GitMirrorStatus.Error, LastFetchError = "auth refused" });
        var cut = Open(Production(), new PipelineProductionAnalysisDto
        {
            Readable = false,
            ExternalSource = true,
            SourceProblem = ProductionSourceProblem.ExternalNotSynchronized
        });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Action)));

        await cut.Find(Action).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains("PipelineSetupMirrorSyncFailed",
            cut.Find("[data-testid='pipeline-production-completion-error']").TextContent, StringComparison.Ordinal));
        Assert.Empty(Calls(Apply));
    }

    [Fact]
    public void TheYamlHelper_AddsOnlyWhatTheDefinitionLacks_AndNeverChangesAValueItHolds()
    {
        var yaml = "name: x\nvariables:\n  COMPOSE_FILE: \"mine.yml\"\nstages: []";

        var completed = PipelineProductionYaml.WithMissingVariables(yaml, "other.yml", "blue-green");

        Assert.Contains("COMPOSE_FILE: \"mine.yml\"", completed, StringComparison.Ordinal);
        Assert.DoesNotContain("other.yml", completed, StringComparison.Ordinal);
        Assert.Contains("DEPLOY_STRATEGY: \"blue-green\"", completed, StringComparison.Ordinal);
        Assert.Equal(completed, PipelineProductionYaml.WithMissingVariables(completed, "other.yml", "blue-green"));
        Assert.Equal("blue-green", PipelineProductionYaml.ReadVariable(completed, "DEPLOY_STRATEGY"));
    }
}
