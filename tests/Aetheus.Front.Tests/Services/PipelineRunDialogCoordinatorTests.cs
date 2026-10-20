// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Services;

public sealed class PipelineRunDialogCoordinatorTests : BunitContext
{
    [Fact]
    public async Task ConfigureLaunchAsync_RefreshesBranchesAndSelectsRepositoryDefault()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto
        {
            RepositoryId = 9,
            Branch = "develop"
        });
        handler.SetJsonResponse("api/pipelines/5", new PipelineDto { Id = 5, Name = "app-ci" });
        handler.SetJsonResponse(
            HttpMethod.Get,
            "api/git/repos/9",
            new GitLightRepoDto { Id = 9, Name = "app", DefaultBranch = "main" });
        handler.SetPaginatedJsonResponse(
            HttpMethod.Get,
            "api/git/repos/9/branches",
            new List<GitLightBranchDto>
            {
                new() { Name = "develop" },
                new() { Name = "main", IsDefault = true }
            });
        handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);
        var openedParameters = dialog.LastParameters;

        Assert.NotNull(openedParameters);
        Assert.Equal("main", openedParameters["SourceBranch"]);
        var branches = Assert.IsType<List<GitLightBranchDto>>(openedParameters["Branches"]);
        Assert.Collection(
            branches,
            branch =>
            {
                Assert.Equal("main", branch.Name);
                Assert.True(branch.IsDefault);
            },
            branch => Assert.Equal("develop", branch.Name));
        Assert.Contains(handler.Requests, request =>
            request.Method == "GET"
            && request.Url.Contains("api/git/repos/9/branches", StringComparison.Ordinal)
            && request.Url.Contains("page=1", StringComparison.Ordinal)
            && request.Url.Contains("pageSize=100", StringComparison.Ordinal));
    }

    /// <summary>The unified run dialog includes parameters without a separate branch-selection
    /// step.</summary>
    [Fact]
    public async Task ConfigureLaunchAsync_CarriesTheDeclaredParametersIntoTheSameDialog()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto { RepositoryId = 0 });
        handler.SetJsonResponse("api/pipelines/5", new PipelineDto { Id = 5, Name = "app-ci" });
        handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>
        {
            new() { Name = "flavour", DisplayName = "Flavour", Type = "string", Default = "fast" }
        });

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        var declared = Assert.IsType<List<PipelineRunParameterDto>>(dialog.LastParameters!["Parameters"]);
        Assert.Equal("flavour", Assert.Single(declared).Name);
    }

    [Fact]
    public async Task ResolveDefaultParametersAsync_RequestFailure_BlocksPipelineLaunch()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetResponse("api/pipelines/5/parameters", System.Net.HttpStatusCode.BadGateway);

        var result = await Services.GetRequiredService<PipelineRunDialogCoordinator>()
            .ResolveDefaultParametersAsync(5);

        Assert.False(result.Proceed);
        Assert.Null(result.Parameters);
    }

    /// <summary>
    /// The point of the change: a pipeline whose parameters all have defaults launches straight away,
    /// with those defaults, and nothing is asked of the user.
    /// </summary>
    [Fact]
    public async Task ResolveDefaultParametersAsync_AllDefaulted_TakesThemWithoutAsking()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>
        {
            new() { Name = "flavour", DisplayName = "Flavour", Type = "string", Default = "fast" },
            new() { Name = "dryRun", DisplayName = "Dry run", Type = "boolean", Default = "false", Required = true }
        });
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

        var result = await Services.GetRequiredService<PipelineRunDialogCoordinator>()
            .ResolveDefaultParametersAsync(5);

        Assert.True(result.Proceed);
        Assert.Null(dialog.LastComponent);
        Assert.Equal("fast", result.Parameters!["flavour"]);
        Assert.Equal("false", result.Parameters["dryRun"]);
    }

    /// <summary>
    /// M-051: the Deploy contract declares candidateVersion required with no default, so the launch
    /// still stops for an explicit choice. Nothing is ever selected silently on the user's behalf.
    /// </summary>
    [Fact]
    public async Task ResolveDefaultParametersAsync_RequiredWithoutDefault_StillAsks()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>
        {
            new() { Name = "candidateVersion", DisplayName = "Candidate", Type = "string", Required = true }
        });
        handler.SetJsonResponse("api/pipelines/5", new PipelineDto { Id = 5, Name = "deploy" });
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = null;

        var result = await Services.GetRequiredService<PipelineRunDialogCoordinator>()
            .ResolveDefaultParametersAsync(5);

        Assert.NotNull(dialog.LastComponent);
        Assert.False(result.Proceed);
    }

    /// <summary>
    /// The deploy dialog shows the project's restorable releases next to the version field: the value
    /// stays typed and confirmed (M-051), the list is a picking aid.
    /// </summary>
    [Fact]
    public async Task ResolveDefaultParametersAsync_ProjectPipeline_OffersItsReleases()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>
        {
            new() { Name = "candidateVersion", DisplayName = "Candidate", Type = "string", Required = true }
        });
        handler.SetJsonResponse("api/pipelines/5", new PipelineDto { Id = 5, Name = "deploy", ProjectId = 3 });
        handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 1, Version = "1.1.482", Status = ReleaseStatus.Published }],
            TotalCount = 1
        });
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = null;

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ResolveDefaultParametersAsync(5);

        var offered = Assert.IsType<List<ReleaseDto>>(dialog.LastParameters!["AvailableReleases"]);
        Assert.Equal("1.1.482", Assert.Single(offered).Version);
        Assert.Equal(1, dialog.LastParameters!["AvailableReleaseTotal"]);
        // Every release whose payload is retained, in one request the grid then pages.
        Assert.Contains(handler.Requests, r => r.Url.Contains("api/releases") && r.Url.Contains("projectId=3")
            && r.Url.Contains("pageSize=200") && r.Url.Contains("deployable=true"));
    }

    /// <summary>A pipeline without a project shows nothing and, above all, fetches nothing.</summary>
    [Fact]
    public async Task ResolveDefaultParametersAsync_NoProject_FetchesNoRelease()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>
        {
            new() { Name = "note", DisplayName = "Note", Type = "string", Required = true }
        });
        handler.SetJsonResponse("api/pipelines/5", new PipelineDto { Id = 5, Name = "ci" });
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = null;

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ResolveDefaultParametersAsync(5);

        Assert.Empty(Assert.IsType<List<ReleaseDto>>(dialog.LastParameters!["AvailableReleases"]));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("api/releases"));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Releases_AreFetchedOnlyWhenAParameterTakesAVersion(bool versionParameter, int releaseCalls)
    {
        // Fetch releases only when the pipeline accepts a version, once per dialog.
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto());
        handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", versionParameter
            ? new List<PipelineRunParameterDto> { new() { Name = "candidateVersion", Type = "string", Required = true } }
            : new List<PipelineRunParameterDto> { new() { Name = "dryRun", Type = "boolean", Default = "false" } });
        handler.SetJsonResponse("api/pipelines/5", new PipelineDto { Id = 5, Name = "app", ProjectId = 3 });
        handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto> { Items = [new ReleaseDto { Id = 1, Version = "1.0.0" }] });

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        Assert.Equal(releaseCalls, handler.Requests.Count(request => request.Url.Contains("api/releases", StringComparison.Ordinal)));
        Assert.Equal(versionParameter, dialog.LastParameters!["AvailableReleases"] is not null);
    }

    /// <summary>"Run to end" is offered only when the backend says the platform allows it, and an
    /// unreadable answer (an older backend, an outage) hides it rather than blocking the launch.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public async Task ConfigureLaunchAsync_OffersRunToEndOnlyWhenTheBackendAllowsIt(bool? allowed, bool offered)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto { RepositoryId = 0 });
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        if (allowed is { } value)
            handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto { RunToEndAllowed = value });
        else
            handler.SetResponse("api/pipelines/run-options", System.Net.HttpStatusCode.ServiceUnavailable);

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        Assert.NotNull(dialog.LastParameters);
        Assert.Equal(offered, dialog.LastParameters["RunToEndAllowed"]);
    }
}
