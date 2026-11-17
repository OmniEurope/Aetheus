// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-38 (project oe1, external source whose Compose file could not be read): the production preparation used to write
/// nothing when no Compose file was read, so the four Apache templates never reached the configuration repository. The wizard now
/// asks, and creates them through the Apache-only endpoint (the setup's own, which writes only to the configuration repository
/// Aetheus manages). The server side is proved in <c>PipelineProductionSetupApacheWithoutComposeTests</c>; this proves what the
/// wizard sends and what it tells the user.</summary>
public sealed class PipelineSetupProductionApacheTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";
    private const string Apache = "api/pipelines/setup/production-apache";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] AllFour =
    [
        ".pipeline/configs/apache/api-http.conf", ".pipeline/configs/apache/api-https.conf",
        ".pipeline/configs/apache/app-http.conf", ".pipeline/configs/apache/app-https.conf"
    ];

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupProductionApacheTests() => _handler = BunitTestHelper.RegisterServices(this);

    private PipelineSetupProduction Setup(bool monolith = false) =>
        new(Services.GetRequiredService<ApiClient>(), () => { }, () => "main", () => monolith);

    /// <summary>The state oe1 was left in: an external source whose mirror holds no commit, so nothing could be read.</summary>
    private void TheExternalSourceCannotBeRead(List<string>? missing = null)
    {
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, new PipelineProductionAnalysisDto
        {
            Readable = false,
            ExternalSource = true,
            SourceProblem = ProductionSourceProblem.ExternalNotSynchronized,
            MissingApacheTemplates = missing ?? [.. AllFour]
        });
        _handler.SetJsonResponse(HttpMethod.Post, Apache, new ProjectConfigRepositoryDto
        {
            Id = 9,
            Name = "oe1-config",
            Slug = "oe1-config",
            Created = true,
            FilesAdded = [.. AllFour]
        });
    }

    private IEnumerable<string> PostBodies(string urlEnd) => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method && request.Url.EndsWith(urlEnd, StringComparison.OrdinalIgnoreCase))
        .Select(request => request.Body!);

    [Fact]
    public async Task WithoutAReadableComposeFile_TheApacheTemplatesAreAskedFor_AndCreatedByTheApacheOnlyEndpoint()
    {
        TheExternalSourceCannotBeRead();
        var setup = Setup();
        await setup.LoadAsync(21);

        Assert.True(setup.ApacheAsk);
        Assert.Equal(AllFour, setup.ApacheToCreate);

        var prepared = await setup.PrepareAsync(21, production: true, qa: false, "failed");

        var request = JsonSerializer.Deserialize<PipelineProductionApacheRequest>(Assert.Single(PostBodies(Apache)), Web)!;
        Assert.Equal(21, request.ProjectId);
        Assert.False(request.Monolith);
        Assert.Equal(ProductionStrategy.Replace, request.Strategy);
        Assert.Empty(PostBodies(Apply));
        Assert.Equal(string.Join(", ", AllFour), prepared.ConfigFilesAdded);
        // Production still says it was not prepared: only the Apache templates were, the Compose file is still to find.
        Assert.True(prepared.NotPrepared);
    }

    [Fact]
    public async Task TheStrategyTheUserChose_IsTheOneTheTemplatesAreCreatedFor()
    {
        TheExternalSourceCannotBeRead();
        var setup = Setup();
        await setup.LoadAsync(21);
        setup.ChooseStrategy(ProductionStrategy.Replace);

        await setup.PrepareAsync(21, production: true, qa: false, "failed");

        Assert.Equal(ProductionStrategy.Replace, JsonSerializer.Deserialize<PipelineProductionApacheRequest>(Assert.Single(PostBodies(Apache)), Web)!.Strategy);
    }

    [Fact]
    public async Task AMonolith_AsksTheServerForTheReplacementTemplates()
    {
        TheExternalSourceCannotBeRead();
        var setup = Setup(monolith: true);
        await setup.LoadAsync(21);

        await setup.PrepareAsync(21, production: true, qa: false, "failed");

        var request = JsonSerializer.Deserialize<PipelineProductionApacheRequest>(Assert.Single(PostBodies(Apache)), Web)!;
        Assert.True(request.Monolith);
        Assert.Equal(ProductionStrategy.Replace, request.Strategy);
    }

    [Fact]
    public async Task WhenTheUserDeclines_NothingIsCreated()
    {
        TheExternalSourceCannotBeRead();
        var setup = Setup();
        await setup.LoadAsync(21);
        setup.ChooseCreateApacheTemplates(false);

        var prepared = await setup.PrepareAsync(21, production: true, qa: false, "failed");

        Assert.Empty(PostBodies(Apache));
        Assert.Equal(string.Empty, prepared.ConfigFilesAdded);
    }

    [Fact]
    public async Task WhenNothingIsMissing_NothingIsAskedNorCreated()
    {
        TheExternalSourceCannotBeRead(missing: []);
        var setup = Setup();
        await setup.LoadAsync(21);

        var prepared = await setup.PrepareAsync(21, production: true, qa: false, "failed");

        Assert.False(setup.ApacheAsk);
        Assert.Empty(PostBodies(Apache));
        Assert.Equal(string.Empty, prepared.ConfigFilesAdded);
    }

    [Fact]
    public async Task ARefusalOfTheServer_StopsThePreparationWithItsOwnWords()
    {
        TheExternalSourceCannotBeRead();
        _handler.SetRawResponse(HttpMethod.Post, Apache,
            "{\"message\":\"The configuration files could not be committed.\"}", "application/json", HttpStatusCode.BadRequest);
        var setup = Setup();
        await setup.LoadAsync(21);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => setup.PrepareAsync(21, production: true, qa: false, "failed"));

        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }

    [Fact]
    public async Task WithAReadableComposeFile_TheFilesTheSetupCreatedAreNamed_AndTheApacheOnlyEndpointIsNotUsed()
    {
        const string compose = "deploy/compose/remote.compose.yml";
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, new PipelineProductionAnalysisDto
        {
            Readable = true,
            ComposeFiles = [compose],
            ComposeFile = compose,
            DeclaresBackPort = true,
            MissingApacheTemplates = [.. AllFour]
        });
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto
        {
            ComposeFile = compose,
            Strategy = ProductionStrategy.Replace,
            DeclaresApi = true,
            ConfigRepository = new ProjectConfigRepositoryDto { Id = 9, Slug = "oe1-config", Created = true, FilesAdded = [.. AllFour] }
        });
        var setup = Setup();
        await setup.LoadAsync(21);

        // The setup lists what it will create, and does not ask: it creates them with the rest.
        Assert.False(setup.ApacheAsk);
        Assert.Equal(AllFour, setup.ApacheToCreate);
        var prepared = await setup.PrepareAsync(21, production: true, qa: false, "failed");

        Assert.Empty(PostBodies(Apache));
        Assert.Single(PostBodies(Apply));
        Assert.Equal(string.Join(", ", AllFour), prepared.ConfigFilesAdded);
        Assert.False(prepared.NotPrepared);
    }
}
