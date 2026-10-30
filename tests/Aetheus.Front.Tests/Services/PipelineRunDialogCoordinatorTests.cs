// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

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
        // R-47: abandoning the launch is never silent.
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains("RunParametersReadFailed", toast.Detail, StringComparison.Ordinal);
    }

    private const string InvalidYamlBody =
        """{"message":"The pipeline YAML is invalid: While scanning a plain scalar value, found invalid mapping.","correlationId":"0HN7:00000001"}""";

    /// <summary>R-47, the production incident: the API refuses to read the parameters of a pipeline whose YAML
    /// is invalid (400 with its reason). The launch stops, and the reason reaches the user in one danger
    /// notification (the global notifier is told the caller shows it, so there is no second toast).</summary>
    [Fact]
    public async Task ResolveDefaultParametersAsync_RefusedWithAReason_ShowsTheReasonOnceAndLaunchesNothing()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetRawResponse(HttpMethod.Get, "api/pipelines/5/parameters", InvalidYamlBody, "application/json", HttpStatusCode.BadRequest);

        var result = await Services.GetRequiredService<PipelineRunDialogCoordinator>().ResolveDefaultParametersAsync(5);

        Assert.False(result.Proceed);
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains("The pipeline YAML is invalid: While scanning a plain scalar value", toast.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, request => request.Method == "POST" && request.Url.EndsWith("/run", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "PreflightSessionExpired")]
    [InlineData(HttpStatusCode.Forbidden, "PreflightForbidden")]
    [InlineData(HttpStatusCode.NotFound, "RunNotFound")]
    [InlineData(HttpStatusCode.InternalServerError, "RunParametersReadFailed")]
    public async Task ResolveDefaultParametersAsync_RefusedWithoutAReason_SaysItBySessionAccessOrServer(
        HttpStatusCode status, string expectedKey)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetResponse("api/pipelines/5/parameters", status);

        var result = await Services.GetRequiredService<PipelineRunDialogCoordinator>().ResolveDefaultParametersAsync(5);

        Assert.False(result.Proceed);
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains(expectedKey, toast.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveDefaultParametersAsync_ServerUnreachable_SaysSoAndLaunchesNothing()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetAsyncJsonResponse<object>(
            HttpMethod.Get, "api/pipelines/5/parameters", _ => Task.FromException<object>(new HttpRequestException("connection refused")));

        var result = await Services.GetRequiredService<PipelineRunDialogCoordinator>().ResolveDefaultParametersAsync(5);

        Assert.False(result.Proceed);
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains("RunParametersServerUnreachable", toast.Detail, StringComparison.Ordinal);
    }

    /// <summary>R-47 on the "run with options" path: no dialog opens on a refusal, the reason is shown.</summary>
    [Fact]
    public async Task ConfigureLaunchAsync_ParametersRefusedWithAReason_ShowsItAndOpensNoDialog()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto { RepositoryId = 0 });
        handler.SetRawResponse(HttpMethod.Get, "api/pipelines/5/parameters", InvalidYamlBody, "application/json", HttpStatusCode.BadRequest);

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        var choice = await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        Assert.Null(choice);
        Assert.Null(dialog.LastParameters);
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains("The pipeline YAML is invalid", toast.Detail, StringComparison.Ordinal);
    }

    /// <summary>R-47: picking another branch in the open dialog re-reads the declaration; a refusal keeps the
    /// fields on screen (null) and says why instead of leaving the picker looking unresponsive.</summary>
    [Fact]
    public async Task ConfigureLaunchAsync_ReloadAfterABranchChangeRefused_ShowsTheReasonAndKeepsTheFields()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto { RepositoryId = 0 });
        handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);
        var reload = Assert.IsType<Func<string?, Task<List<PipelineRunParameterDto>?>>>(dialog.LastParameters!["ReloadParameters"]);
        Assert.Empty(Services.Toasts());
        handler.SetRawResponse(HttpMethod.Get, "api/pipelines/5/parameters", InvalidYamlBody, "application/json", HttpStatusCode.BadRequest);

        var reloaded = await reload("feature");

        Assert.Null(reloaded);
        var toast = Assert.Single(Services.Toasts());
        Assert.Contains("The pipeline YAML is invalid", toast.Detail, StringComparison.Ordinal);
    }

    /// <summary>The "run to end" option is a convenience: when it cannot be read the dialog opens without it and
    /// the failure is shown once, by the global notifier when the server answered (404, 503) or by the
    /// coordinator when nothing answered.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ConfigureLaunchAsync_RunOptionsUnreadable_OpensWithoutTheOptionAndShowsOneToast(HttpStatusCode status)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto { RepositoryId = 0 });
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        handler.SetResponse("api/pipelines/run-options", status);
        using var chain = new NotifierChain(this, handler);

        await chain.Coordinator.ConfigureLaunchAsync(5);

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        Assert.NotNull(dialog.LastParameters);
        Assert.Equal(false, dialog.LastParameters["RunToEndAllowed"]);
        Assert.Single(Services.Toasts());
    }

    [Fact]
    public async Task ConfigureLaunchAsync_RunOptionsNeverAnswered_OpensWithoutTheOptionAndShowsOneToast()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto { RepositoryId = 0 });
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        handler.SetAsyncJsonResponse<object>(
            HttpMethod.Get, "api/pipelines/run-options", _ => Task.FromException<object>(new HttpRequestException("connection refused")));
        using var chain = new NotifierChain(this, handler);

        await chain.Coordinator.ConfigureLaunchAsync(5);

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        Assert.Equal(false, dialog.LastParameters!["RunToEndAllowed"]);
        var toast = Assert.Single(Services.Toasts());
        Assert.Contains("RunParametersServerUnreachable", toast.Detail, StringComparison.Ordinal);
    }

    /// <summary>R-47: a failed read of the parameters is one toast, whatever the status. The global notifier
    /// is told the caller handles it, so a 403, a 500 or a 503 does not add its own "Error NNN" beside the
    /// coordinator's sentence.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "PreflightForbidden")]
    [InlineData(HttpStatusCode.InternalServerError, "RunParametersReadFailed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "RunParametersReadFailed")]
    public async Task ResolveDefaultParametersAsync_RefusedThroughTheGlobalNotifier_ShowsExactlyOneToast(
        HttpStatusCode status, string expectedKey)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetResponse("api/pipelines/5/parameters", status);
        using var chain = new NotifierChain(this, handler);

        var result = await chain.Coordinator.ResolveDefaultParametersAsync(5);

        Assert.False(result.Proceed);
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains(expectedKey, toast.Detail, StringComparison.Ordinal);
    }

    /// <summary>A coordinator whose API client goes through the real <see cref="ErrorNotificationHandler"/>,
    /// as in the application, so a test counts the toasts a user would see.</summary>
    private sealed class NotifierChain : IDisposable
    {
        private readonly ErrorNotificationHandler _notifier;
        private readonly HttpClient _http;

        public NotifierChain(BunitContext context, BunitTestHelper.TestHandler handler)
        {
            var toast = context.Services.GetRequiredService<NotifyHelper>();
            var localizer = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>();
            _notifier = new ErrorNotificationHandler(toast, localizer) { InnerHandler = handler };
            _http = new HttpClient(_notifier, disposeHandler: false) { BaseAddress = new Uri("http://test/") };
            Coordinator = new PipelineRunDialogCoordinator(
                new ApiClient(_http), context.Services.GetRequiredService<OmniDialogService>(), toast, localizer);
        }

        public PipelineRunDialogCoordinator Coordinator { get; }

        public void Dispose()
        {
            _http.Dispose();
            _notifier.Dispose();
        }
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

    private BunitTestHelper.TestHandler? _handler;

    private static List<PipelineRunParameterDto> DeployParameters(string serverType = "string", string? serverDefault = null) =>
    [
        new() { Name = "candidateVersion", DisplayName = "Candidate", Type = "string", Required = true },
        new() { Name = "deployServer", DisplayName = "Production server", Type = serverType, Required = true, Default = serverDefault }
    ];

    /// <summary>The deployment pipeline of project 3, whose servers are the project's own (<paramref name="projectServers"/>) and those
    /// of its production environment (<paramref name="productionServers"/>), the way W6 has them (recette R-76, R-83).</summary>
    private ImmediateDialogService ArrangeDeployDialog(
        List<PipelineRunParameterDto> parameters, string[] projectServers, string[] productionServers)
    {
        var handler = _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto());
        handler.SetJsonResponse("api/pipelines/run-options", new PipelineRunOptionsDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", parameters);
        handler.SetJsonResponse("api/pipelines/5", new PipelineDto { Id = 5, Name = "w6-deploy-prod", ProjectId = 3 });
        handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 1, Version = "c-4310fadf", Status = ReleaseStatus.Published }],
            TotalCount = 1
        });
        handler.SetPaginatedJsonResponse("api/projects/3/servers", projectServers.Select((name, index) =>
            new ProjectServerDto { Id = index + 1, ProjectId = 3, ServerId = index + 10, ServerName = name }));
        handler.SetPaginatedJsonResponse("api/environments", productionServers.Length == 0
            ? []
            : new[]
            {
                new EnvironmentDto
                {
                    Id = 8, Name = "prod", ProjectId = 3,
                    Servers = [.. productionServers.Select((name, index) => new EnvironmentServerDto { ServerId = index + 10, ServerName = name })]
                }
            });
        return (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private static PipelineRunParameterDto DeployServerOf(ImmediateDialogService dialog) =>
        Assert.IsType<List<PipelineRunParameterDto>>(dialog.LastParameters!["Parameters"]).Single(item => item.Name == "deployServer");

    /// <summary>Recette R-76: the run dialog of a deployment pipeline showed two empty fields. The release list is loaded for the
    /// version even though the server parameter is also required without a default, and the server is offered among the project's.</summary>
    [Fact]
    public async Task ADeployDialog_WithTheVersionAndTheServerBothEmpty_LoadsTheReleasesForTheVersion()
    {
        var dialog = ArrangeDeployDialog(DeployParameters(), [], ["vps-prod"]);

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        var offered = Assert.IsType<List<ReleaseDto>>(dialog.LastParameters!["AvailableReleases"]);
        Assert.Equal("c-4310fadf", Assert.Single(offered).Version);
    }

    /// <summary>W6: the server is attached to the production environment only, the project holds none of its own.</summary>
    [Fact]
    public async Task AnEmptyDeployServer_IsOfferedAsAChoice_WithTheProductionEnvironmentsServerPreselected()
    {
        var dialog = ArrangeDeployDialog(DeployParameters(), [], ["vps-prod"]);

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        var server = DeployServerOf(dialog);
        Assert.Equal("choice", server.Type);
        Assert.Equal(["vps-prod"], server.AllowedValues);
        Assert.Equal("vps-prod", server.Default);
    }

    [Fact]
    public async Task WithSeveralServersAndNoProductionEnvironment_NoneIsGuessed_TheUserChoosesAmongThem()
    {
        var dialog = ArrangeDeployDialog(DeployParameters(), ["vps-b", "vps-a"], []);

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        var server = DeployServerOf(dialog);
        Assert.Equal("choice", server.Type);
        Assert.Equal(["vps-a", "vps-b"], server.AllowedValues);
        Assert.Null(server.Default);
    }

    [Fact]
    public async Task TheProjectsOnlyServer_IsPreselected_WhenItsEnvironmentHoldsNone()
    {
        var dialog = ArrangeDeployDialog(DeployParameters(), ["vps-a"], []);

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        Assert.Equal("vps-a", DeployServerOf(dialog).Default);
    }

    [Fact]
    public async Task AProjectWithNoServerAtAll_KeepsTheServerParameterAsItWas()
    {
        var dialog = ArrangeDeployDialog(DeployParameters(), [], []);

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        var server = DeployServerOf(dialog);
        Assert.Equal("string", server.Type);
        Assert.Empty(server.AllowedValues);
        Assert.Null(server.Default);
    }

    /// <summary>A pipeline the wizard wrote with its servers already is a choice with a default: nothing is read or changed.</summary>
    [Fact]
    public async Task AServerParameterThatIsAlreadyAChoice_IsLeftAlone_AndNoServerIsRead()
    {
        var parameters = DeployParameters("choice", "vps-a");
        parameters[1] = parameters[1] with { AllowedValues = ["vps-a", "vps-b"] };
        var dialog = ArrangeDeployDialog(parameters, ["vps-z"], ["vps-z"]);

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        var server = DeployServerOf(dialog);
        Assert.Equal(["vps-a", "vps-b"], server.AllowedValues);
        Assert.Equal("vps-a", server.Default);
        Assert.DoesNotContain(
            _handler!.Requests,
            request => request.Url.Contains("api/environments", StringComparison.Ordinal)
                || request.Url.Contains("api/projects/3/servers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePlainRun_AlsoOffersTheServers_WhenTheVersionStillNeedsAnAnswer()
    {
        var dialog = ArrangeDeployDialog(DeployParameters(), ["vps-a", "vps-b"], ["vps-b"]);
        dialog.OpenResult = null;

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ResolveDefaultParametersAsync(5);

        var server = DeployServerOf(dialog);
        Assert.Equal(["vps-a", "vps-b"], server.AllowedValues);
        Assert.Equal("vps-b", server.Default);
        Assert.IsType<List<ReleaseDto>>(dialog.LastParameters!["AvailableReleases"]);
    }

    /// <summary>The project's servers and its environments are read concurrently: the project-servers answer is held until the
    /// environments request has been sent, which only happens when both requests are in flight together. The bound only stops a
    /// sequential read from hanging the run.</summary>
    [Fact]
    public async Task TheProjectServersAndTheEnvironments_AreReadConcurrently()
    {
        var dialog = ArrangeDeployDialog(DeployParameters(), [], ["vps-prod"]);
        var handler = _handler!;
        handler.SetAsyncJsonResponse(HttpMethod.Get, "api/projects/3/servers", async ct =>
        {
            await handler.WhenGetAsync("api/environments").WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new PaginatedResult<ProjectServerDto> { Items = [], TotalCount = 0, Page = 1, PageSize = 1 };
        });

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        var server = DeployServerOf(dialog);
        Assert.Equal("choice", server.Type);
        Assert.Equal("vps-prod", server.Default);
    }

    /// <summary>Reading the servers is a picking aid like the release list: failing to read them never blocks the launch.</summary>
    [Fact]
    public async Task WhenTheServersCannotBeRead_TheLaunchDialogOpensAllTheSame_WithTheParameterAsItWas()
    {
        var dialog = ArrangeDeployDialog(DeployParameters(), [], []);
        _handler!.SetResponse("api/projects/3/servers", System.Net.HttpStatusCode.BadGateway);

        await Services.GetRequiredService<PipelineRunDialogCoordinator>().ConfigureLaunchAsync(5);

        Assert.Equal("string", DeployServerOf(dialog).Type);
    }
}
