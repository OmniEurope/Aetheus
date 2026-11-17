// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aetheus.Front.Tests;

public class ApiClientTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static (ApiClient client, TestHandler handler) Create()
    {
        var handler = new TestHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        return (new ApiClient(http), handler);
    }

    private static StringContent Json<T>(T obj) =>
        new(JsonSerializer.Serialize(obj, JsonOpts), System.Text.Encoding.UTF8, "application/json");

    // --- Auth ---

    [Fact]
    public async Task LoginAsync_Success_ReturnsToken()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new LoginResponse { Token = "tok" }) };
        var result = await api.Auth.LoginAsync(new LoginRequest { Email = "u@example.com", Password = "p" }, Xunit.TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("tok", result.Value!.Token);
        h.AssertSent(HttpMethod.Post, "api/auth/login", json: """{"email":"u@example.com","password":"p","rememberMe":false,"totpCode":null,"recoveryCode":null}""");
    }

    [Fact]
    public async Task LoginAsync_Failure_PreservesStatusAndError()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = Json(new ApiError { Message = "Invalid credentials." })
        };
        var result = await api.Auth.LoginAsync(new LoginRequest { Email = "u@example.com", Password = "p" }, Xunit.TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal("Invalid credentials.", result.Error?.Message);
        h.AssertSent(HttpMethod.Post, "api/auth/login", json: """{"email":"u@example.com","password":"p","rememberMe":false,"totpCode":null,"recoveryCode":null}""");
    }

    [Fact]
    public async Task GetRegistrationTokensAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new List<RegistrationTokenDto> { new() { Id = 1, Token = "t" } })
        };
        var result = await api.Auth.GetRegistrationTokensAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.Single(result);
        h.AssertSent(HttpMethod.Get, "api/auth/registration-tokens");
    }

    [Fact]
    public async Task CreateRegistrationTokenAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new RegistrationTokenDto { Id = 1, Token = "new" })
        };
        var result = await api.Auth.CreateRegistrationTokenAsync(48, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Post, "api/auth/registration-tokens", json: """{"expirationHours":48,"organizationId":null}""");
    }

    [Fact]
    public async Task CreateRegistrationTokenAsync_Failure_ReturnsNull()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        var result = await api.Auth.CreateRegistrationTokenAsync(ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Null(result);
        h.AssertSent(HttpMethod.Post, "api/auth/registration-tokens", json: """{"expirationHours":24,"organizationId":null}""");
    }

    // --- Servers ---

    [Fact]
    public async Task GetServersAsync_ReturnsPaginatedResult()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ServerDto> { Items = [new() { Id = 1, Name = "s1" }], TotalCount = 1 })
        };
        var result = await api.Servers.GetServersAsync(1, 25, "test", ServerType.Docker, ServerStatus.Online, "Name", true);
        Assert.Single(result.Items);
        Assert.Contains("search=test", h.LastRequest!.RequestUri!.ToString());
        Assert.Contains("type=Docker", h.LastRequest.RequestUri.ToString());
        Assert.Contains("status=Online", h.LastRequest.RequestUri.ToString());
        Assert.Contains("sortBy=Name", h.LastRequest.RequestUri.ToString());
        h.AssertSent(HttpMethod.Get, "api/servers?page=1&pageSize=25&search=test&type=Docker&status=Online&sortBy=Name&sortDescending=True");
    }

    [Fact]
    public async Task GetServerDetailAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new ServerDetailDto { Id = 5, Name = "srv" })
        };
        var result = await api.Servers.GetServerDetailAsync(5, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(5, result!.Id);
        h.AssertSent(HttpMethod.Get, "api/servers/5");
    }

    [Fact]
    public async Task UpdateServerAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ServerDto { Id = 1 }) };
        var result = await api.Servers.UpdateServerAsync(1, new UpdateServerRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Put, "api/servers/1", new UpdateServerRequest());
    }

    [Fact]
    public async Task UpdateServerAsync_Failure_ReturnsNull()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        var result = await api.Servers.UpdateServerAsync(1, new UpdateServerRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.Null(result);
        h.AssertSent(HttpMethod.Put, "api/servers/1", new UpdateServerRequest());
    }

    [Fact]
    public async Task RetireServerAsync_SendsTheDeleteThatRetires_AndReportsSuccess()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Servers.RetireServerAsync(1, Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Delete, h.LastRequest?.Method);
        Assert.Equal("http://test/api/servers/1", h.RequestUris.Single());
        h.AssertSent(HttpMethod.Delete, "api/servers/1");
    }

    [Fact]
    public async Task RetireServerAsync_ReturnsFalse()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.False(await api.Servers.RetireServerAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/servers/1");
    }

    [Fact]
    public async Task PurgeServerAsync_TargetsThePermanentEndpoint()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.True(await api.Servers.PurgeServerAsync(4, Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Delete, h.LastRequest?.Method);
        Assert.Equal("http://test/api/servers/4/permanent", h.RequestUris.Single());
        h.AssertSent(HttpMethod.Delete, "api/servers/4/permanent");
    }

    [Fact]
    public async Task PurgeServerAsync_Conflict_ReturnsFalse()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.Conflict);
        Assert.False(await api.Servers.PurgeServerAsync(4, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/servers/4/permanent");
    }

    [Fact]
    public async Task GetRetiredServersAsync_PagesAndSortsTheRetiredList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<RetiredServerDto> { Items = [new RetiredServerDto { Id = 4, Name = "vps2577917" }], TotalCount = 1 })
        };
        var result = await api.Servers.GetRetiredServersAsync(2, 10, "RetiredAt", true, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(4, Assert.Single(result.Items).Id);
        Assert.Equal("http://test/api/servers/retired?page=2&pageSize=10&sortBy=RetiredAt&sortDescending=True", h.RequestUris.Single());
        h.AssertSent(HttpMethod.Get, "api/servers/retired?page=2&pageSize=10&sortBy=RetiredAt&sortDescending=True");
    }

    // --- Tasks ---

    [Fact]
    public async Task GetTasksAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ServerTaskDto> { Items = [], TotalCount = 0 })
        };
        var result = await api.Pipelines.GetTasksAsync(2, 10);
        Assert.Empty(result.Items);
        h.AssertSent(HttpMethod.Get, "api/tasks?page=2&pageSize=10");
    }

    [Fact]
    public async Task CreateTaskAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ServerTaskDto { Id = 1 }) };
        var result = await api.Pipelines.CreateTaskAsync(new CreateTaskRequest());
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Post, "api/tasks", new CreateTaskRequest());
    }

    [Fact]
    public async Task CreateTaskAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Pipelines.CreateTaskAsync(new CreateTaskRequest()));
        h.AssertSent(HttpMethod.Post, "api/tasks", new CreateTaskRequest());
    }

    [Fact]
    public async Task CancelTaskAsync_PostsToTaskCancelEndpoint()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);

        var result = await api.Pipelines.CancelTaskAsync(4443, Xunit.TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Post, h.LastRequest!.Method);
        Assert.Equal("/api/tasks/4443/cancel", h.LastRequest.RequestUri!.AbsolutePath);
        h.AssertSent(HttpMethod.Post, "api/tasks/4443/cancel");
    }

    // --- Pipelines ---

    [Fact]
    public async Task GetPipelinesAsync_WithFilters()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<PipelineDto> { Items = [], TotalCount = 0 })
        };
        await api.Pipelines.GetPipelinesAsync(search: "build", triggerType: PipelineTriggerType.Webhook);
        Assert.Contains("search=build", h.LastRequest!.RequestUri!.ToString());
        Assert.Contains("triggerType=Webhook", h.LastRequest.RequestUri.ToString());
        h.AssertSent(HttpMethod.Get, "api/pipelines?page=1&pageSize=25&search=build&triggerType=Webhook");
    }

    [Fact]
    public async Task GetPipelineAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineDto { Id = 3 }) };
        var result = await api.Pipelines.GetPipelineAsync(3, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(3, result!.Id);
        h.AssertSent(HttpMethod.Get, "api/pipelines/3");
    }

    [Fact]
    public async Task CreatePipelineAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineDto { Id = 1 }) };
        var result = await api.Pipelines.CreatePipelineAsync(new CreatePipelineRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result.Value);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        h.AssertSent(HttpMethod.Post, "api/pipelines", new CreatePipelineRequest());
    }

    [Fact]
    public async Task CreatePipelineAsync_InvalidYaml_ReturnsValidationErrors()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = Json(new YamlValidationResultDto { IsValid = false, Errors = ["bad stage"] })
        };
        var result = await api.Pipelines.CreatePipelineAsync(new CreatePipelineRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
        Assert.Contains("bad stage", result.Error.Errors);
        h.AssertSent(HttpMethod.Post, "api/pipelines", new CreatePipelineRequest());
    }

    [Fact]
    public async Task UpdatePipelineAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineDto { Id = 2 }) };
        var result = await api.Pipelines.UpdatePipelineAsync(2, new UpdatePipelineRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Id);
        h.AssertSent(HttpMethod.Put, "api/pipelines/2", new UpdatePipelineRequest());
    }

    [Fact]
    public async Task UpdatePipelineAsync_InvalidYaml_ReturnsValidationErrors()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = Json(new YamlValidationResultDto { IsValid = false, Errors = ["bad step"] })
        };
        var result = await api.Pipelines.UpdatePipelineAsync(2, new UpdatePipelineRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
        Assert.Contains("bad step", result.Error.Errors);
        h.AssertSent(HttpMethod.Put, "api/pipelines/2", new UpdatePipelineRequest());
    }

    [Fact]
    public async Task DeletePipelineAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Pipelines.DeletePipelineAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/pipelines/1");
    }

    [Fact]
    public async Task StatusEndpoint_404_ReportsNotFound_AndConvertsToFalse()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NotFound);

        var status = await api.Pipelines.DeletePipelineAsync(1, Xunit.TestContext.Current.CancellationToken);

        Assert.False(status.Success);
        Assert.True(status.NotFound);
        Assert.False(status.Forbidden);
        Assert.False(status); // implicit bool - keeps `if (await Api.X())` call sites working
        h.AssertSent(HttpMethod.Delete, "api/pipelines/1");
    }

    [Fact]
    public async Task StatusEndpoint_403_ReportsForbidden()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        var status = await api.Pipelines.DeletePipelineAsync(1, Xunit.TestContext.Current.CancellationToken);

        Assert.False(status.Success);
        Assert.True(status.Forbidden);
        Assert.False(status.NotFound);
        h.AssertSent(HttpMethod.Delete, "api/pipelines/1");
    }

    [Fact]
    public async Task TriggerPipelineRunAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineRunDto { Id = 10 }) };
        var result = await api.Pipelines.TriggerPipelineRunAsync(5, ct: Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result.Value);
        Assert.Equal(10, result.Value.Id);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.False(result.NotFound);
        AssertTriggerRunSent(h, "api/pipelines/5/run");
    }

    [Fact]
    public async Task TriggerPipelineRunAsync_InvalidYaml_ReturnsValidationErrors()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = Json(new YamlValidationResultDto { IsValid = false, Errors = ["YAML syntax error"] })
        };
        var result = await api.Pipelines.TriggerPipelineRunAsync(5, ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Null(result.Value);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("YAML syntax error", result.Error.Errors);
        Assert.False(result.NotFound);
        AssertTriggerRunSent(h, "api/pipelines/5/run");
    }

    [Fact]
    public async Task TriggerPipelineRunAsync_LegacyStringBadRequest_LeavesItNull()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = Json("The authoritative pipeline YAML is invalid and cannot be run.")
        };

        var result = await api.Pipelines.TriggerPipelineRunAsync(
            5, ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Null(result.Value);
        Assert.Null(result.Error);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        AssertTriggerRunSent(h, "api/pipelines/5/run");
    }

    [Fact]
    public async Task TriggerPipelineRunAsync_NotFound_FlagsNotFound()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        var result = await api.Pipelines.TriggerPipelineRunAsync(5, ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Null(result.Value);
        Assert.Null(result.Error);
        Assert.True(result.NotFound);
        Assert.False(result.IsSuccess);
        AssertTriggerRunSent(h, "api/pipelines/5/run");
    }

    [Fact]
    public async Task GetPipelineRunsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PaginatedResult<PipelineRunDto>()) };
        var result = await api.Pipelines.GetPipelineRunsAsync(1);
        Assert.Empty(result);
        h.AssertSent(HttpMethod.Get, "api/pipelines/1/runs?pageSize=50");
    }

    [Fact]
    public async Task GetPipelineRunAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineRunDto { Id = 7 }) };
        var result = await api.Pipelines.GetPipelineRunAsync(7, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(7, result!.Id);
        h.AssertSent(HttpMethod.Get, "api/pipelines/runs/7");
    }

    // --- Projects ---

    [Fact]
    public async Task GetProjectsAsync_WithSearch()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 })
        };
        await api.Projects.GetProjectsAsync(search: "web", ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Contains("search=web", h.LastRequest!.RequestUri!.ToString());
        h.AssertSent(HttpMethod.Get, "api/projects?page=1&pageSize=25&search=web");
    }

    [Fact]
    public async Task GetProjectDetailAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ProjectDetailDto { Id = 2 }) };
        var result = await api.Projects.GetProjectDetailAsync(2, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(2, result!.Id);
        h.AssertSent(HttpMethod.Get, "api/projects/2");
    }

    [Fact]
    public async Task CreateProjectAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ProjectDto { Id = 1 }) };
        Assert.NotNull(await api.Projects.CreateProjectAsync(new CreateProjectRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/projects", new CreateProjectRequest());
    }

    [Fact]
    public async Task CreateProjectAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Projects.CreateProjectAsync(new CreateProjectRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/projects", new CreateProjectRequest());
    }

    [Fact]
    public async Task UpdateProjectAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ProjectDto { Id = 1 }) };
        Assert.NotNull(await api.Projects.UpdateProjectAsync(1, new UpdateProjectRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/projects/1", new UpdateProjectRequest());
    }

    [Fact]
    public async Task UpdateProjectAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Projects.UpdateProjectAsync(1, new UpdateProjectRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/projects/1", new UpdateProjectRequest());
    }

    [Fact]
    public async Task DeleteProjectAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.Equal(ProjectDeletionOutcome.Deleted, await api.Projects.DeleteProjectAsync(1, [], Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/projects/1");
    }

    // --- Monitoring ---

    [Fact]
    public async Task GetDashboardAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new DashboardOverviewDto()) };
        Assert.NotNull(await api.Monitoring.GetDashboardAsync());
        h.AssertSent(HttpMethod.Get, "api/monitoring/dashboard");
    }

    [Fact]
    public async Task GetServerMetricsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<ServerMetricDto>()) };
        var after = new DateTime(2026, 7, 28, 12, 30, 0, DateTimeKind.Utc);
        var result = await api.Monitoring.GetServerMetricsAsync(
            1,
            48,
            Xunit.TestContext.Current.CancellationToken,
            after,
            take: 250);

        Assert.Empty(result);
        var uri = h.LastRequest!.RequestUri!.ToString();
        Assert.Contains("hours=48", uri, StringComparison.Ordinal);
        Assert.Contains("take=250", uri, StringComparison.Ordinal);
        Assert.Contains("afterUtc=", uri, StringComparison.Ordinal);
        h.AssertSent(HttpMethod.Get, "api/monitoring/servers/1/metrics?hours=48&take=250&afterUtc=2026-07-28T12%3A30%3A00.0000000Z");
    }

    // --- Logs ---

    [Fact]
    public async Task GetTaskLogsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<TaskLogDto>()) };
        var result = await api.Monitoring.GetTaskLogsAsync(5, Xunit.TestContext.Current.CancellationToken);
        Assert.Empty(result);
        h.AssertSent(HttpMethod.Get, "api/logs/task/5");
    }

    // --- Settings ---

    [Fact]
    public async Task GetSettingsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new List<AppSettingDto> { new() { Key = "k", Value = "v" } })
        };
        var result = await api.Settings.GetSettingsAsync();
        Assert.Single(result);
        h.AssertSent(HttpMethod.Get, "api/settings");
    }

    [Fact]
    public async Task UpdateSettingAsync_SendsPut()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        await api.Settings.UpdateSettingAsync("key1", "val1");
        Assert.Equal(HttpMethod.Put, h.LastRequest!.Method);
        h.AssertSent(HttpMethod.Put, "api/settings/key1", json: """{"key":"key1","value":"val1"}""");
    }

    [Fact]
    public async Task GetSecretsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<SecretDto>()) };
        Assert.Empty(await api.Settings.GetSecretsAsync());
        h.AssertSent(HttpMethod.Get, "api/settings/secrets");
    }

    [Fact]
    public async Task CreateSecretAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new SecretDto { Id = 1 }) };
        Assert.NotNull(await api.Settings.CreateSecretAsync(new CreateSecretRequest()));
        h.AssertSent(HttpMethod.Post, "api/settings/secrets", new CreateSecretRequest());
    }

    [Fact]
    public async Task CreateSecretAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Settings.CreateSecretAsync(new CreateSecretRequest()));
        h.AssertSent(HttpMethod.Post, "api/settings/secrets", new CreateSecretRequest());
    }

    [Fact]
    public async Task DeleteSecretAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Settings.DeleteSecretAsync(1));
        h.AssertSent(HttpMethod.Delete, "api/settings/secrets/1");
    }

    // --- Docker ---

    [Fact]
    public async Task GetDockerContainersAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<DockerContainerDto>()) };
        Assert.Empty(await api.ServerTools.GetDockerContainersAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/servers/1/docker/containers");
    }

    [Fact]
    public async Task ExecuteDockerActionAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.ExecuteDockerActionAsync(1, new DockerActionRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/action", new DockerActionRequest());
    }

    [Fact]
    public async Task GetContainerLogsAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("log line") };
        var result = await api.ServerTools.GetContainerLogsAsync(1, new DockerContainerLogsRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("log line", result);
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/containers/logs", new DockerContainerLogsRequest());
    }

    [Fact]
    public async Task GetContainerLogsAsync_Failure_ReturnsEmpty()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Equal(string.Empty, await api.ServerTools.GetContainerLogsAsync(1, new DockerContainerLogsRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/containers/logs", new DockerContainerLogsRequest());
    }

    [Fact]
    public async Task GetDockerImagesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<DockerImageDto>()) };
        Assert.Empty(await api.ServerTools.GetDockerImagesAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/docker/images");
    }

    [Fact]
    public async Task PullDockerImageAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.PullDockerImageAsync(1, new DockerPullImageRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/images/pull", new DockerPullImageRequest());
    }

    [Fact]
    public async Task RemoveDockerImageAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.RemoveDockerImageAsync(1, "img123", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/servers/1/docker/images/img123");
    }

    [Fact]
    public async Task GetComposeStacksAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<DockerComposeStackDto>()) };
        Assert.Empty(await api.ServerTools.GetComposeStacksAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/docker/compose");
    }

    [Fact]
    public async Task ExecuteComposeActionAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.ExecuteComposeActionAsync(1, new DockerComposeActionRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/compose/action", new DockerComposeActionRequest());
    }

    [Fact]
    public async Task GetDockerNetworksAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<DockerNetworkDto>()) };
        Assert.Empty(await api.ServerTools.GetDockerNetworksAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/docker/networks");
    }

    [Fact]
    public async Task GetDockerVolumesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<DockerVolumeDto>()) };
        Assert.Empty(await api.ServerTools.GetDockerVolumesAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/docker/volumes");
    }

    [Fact]
    public async Task PruneDockerAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.PruneDockerAsync(1, new DockerPruneRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/prune", new DockerPruneRequest());
    }

    [Fact]
    public async Task UpdateDockerResourceLimitsAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.UpdateDockerResourceLimitsAsync(1, new DockerResourceLimitsRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/resource-limits", new DockerResourceLimitsRequest());
    }

    [Fact]
    public async Task InspectContainerAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.InspectContainerAsync(1, "cid", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/containers/cid/inspect");
    }

    [Fact]
    public async Task GetComposeFileAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.GetComposeFileAsync(1, "stack1", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/servers/1/docker/compose/stack1/file");
    }

    [Fact]
    public async Task SaveComposeFileAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.SaveComposeFileAsync(1, new DockerComposeFileSaveRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/servers/1/docker/compose/file", new DockerComposeFileSaveRequest());
    }

    [Fact]
    public async Task ExecuteShellCommandAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.ExecuteShellCommandAsync(1, new DockerExecRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/exec", new DockerExecRequest());
    }

    [Fact]
    public async Task GetContainerEnvVarsAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.GetContainerEnvVarsAsync(1, "cid", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/containers/cid/env");
    }

    [Fact]
    public async Task ListContainerFilesAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.ListContainerFilesAsync(1, new DockerBrowseRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/containers/browse", new DockerBrowseRequest());
    }

    [Fact]
    public async Task BuildImageAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.BuildImageAsync(1, new DockerBuildRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/docker/build", new DockerBuildRequest());
    }

    // --- Server Configuration ---

    [Fact]
    public async Task ExportServerConfigAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("yaml: content") };
        var result = await api.Servers.ExportServerConfigAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("yaml: content", result);
        h.AssertSent(HttpMethod.Get, "api/servers/1/configuration/export");
    }

    [Fact]
    public async Task ExportServerConfigAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await api.Servers.ExportServerConfigAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/servers/1/configuration/export");
    }

    [Fact]
    public async Task ValidateServerConfigAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ServerConfigValidationResult()) };
        Assert.NotNull(await api.Servers.ValidateServerConfigAsync(1, "yaml", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/configuration/validate", json: """{"yaml":"yaml"}""");
    }

    [Fact]
    public async Task ValidateServerConfigAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Servers.ValidateServerConfigAsync(1, "yaml", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/configuration/validate", json: """{"yaml":"yaml"}""");
    }

    [Fact]
    public async Task PreviewServerConfigAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ServerConfigPreviewDto()) };
        Assert.NotNull(await api.Servers.PreviewServerConfigAsync(1, "yaml", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/configuration/preview", json: """{"yaml":"yaml"}""");
    }

    [Fact]
    public async Task PreviewServerConfigAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Servers.PreviewServerConfigAsync(1, "yaml", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/configuration/preview", json: """{"yaml":"yaml"}""");
    }

    [Fact]
    public async Task DeployServerConfigAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ServerConfigDeployResultDto { TasksCreated = 3 }) };
        var result = await api.Servers.DeployServerConfigAsync(1, "yaml", Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(3, result!.TasksCreated);
        h.AssertSent(HttpMethod.Post, "api/servers/1/configuration/deploy", json: """{"yaml":"yaml"}""");
    }

    [Fact]
    public async Task DeployServerConfigAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Servers.DeployServerConfigAsync(1, "yaml", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/configuration/deploy", json: """{"yaml":"yaml"}""");
    }

    // --- Server Sub-Resources ---

    [Fact]
    public async Task GetServerNamesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<string> { "srv1", "srv2" }) };
        var result = await api.Servers.GetServerNamesAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        h.AssertSent(HttpMethod.Get, "api/servers/names");
    }

    [Fact]
    public async Task GetServerProjectsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ProjectDto>())
        };
        Assert.Empty(await api.Servers.GetServerProjectsAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/projects?page=1&pageSize=100&sortDescending=false");
    }

    [Fact]
    public async Task GetServerPipelinesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<PipelineDto>()) };
        Assert.Empty(await api.Pipelines.GetServerPipelinesAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/pipelines");
    }

    [Fact]
    public async Task GetServerVariableLibrariesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<VariableLibraryDto>()) };
        Assert.Empty(await api.Servers.GetServerVariableLibrariesAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/variable-libraries");
    }

    [Fact]
    public async Task GetServerVaultsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<VaultDto>()) };
        Assert.Empty(await api.Servers.GetServerVaultsAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/vaults");
    }

    [Fact]
    public async Task GetServerReleasesAsync_ReturnsPaginated()
    {
        // A360-18: the route is paginated now, so the client returns a page and its total instead of
        // pulling every release the server ever touched into the browser.
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ReleaseDto> { Items = [], TotalCount = 0, Page = 1, PageSize = 25 })
        };

        var result = await api.Servers.GetServerReleasesAsync(1);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        h.AssertSent(HttpMethod.Get, "api/servers/1/releases?page=1&pageSize=25");
    }

    [Fact]
    public async Task GetServerTasksAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ServerTaskDto> { Items = [], TotalCount = 0 })
        };
        var result = await api.Servers.GetServerTasksAsync(1);
        Assert.Empty(result.Items);
        h.AssertSent(HttpMethod.Get, "api/servers/1/tasks?page=1&pageSize=25");
    }

    [Fact]
    public async Task GetServerLogsAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<TaskLogDto> { Items = [], TotalCount = 0 })
        };
        var result = await api.Servers.GetServerLogsAsync(1);
        Assert.Empty(result.Items);
        h.AssertSent(HttpMethod.Get, "api/servers/1/logs?page=1&pageSize=50");
    }

    // --- Server Modules ---

    [Fact]
    public async Task GetServerModulesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<ServerModuleDto>()) };
        Assert.Empty(await api.Servers.GetServerModulesAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/modules");
    }

    [Fact]
    public async Task CreateServerModuleAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ServerModuleDto { Id = 1 }) };
        Assert.NotNull(await api.Servers.CreateServerModuleAsync(1, new CreateServerModuleRequest()));
        h.AssertSent(HttpMethod.Post, "api/servers/1/modules", new CreateServerModuleRequest());
    }

    [Fact]
    public async Task CreateServerModuleAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Servers.CreateServerModuleAsync(1, new CreateServerModuleRequest()));
        h.AssertSent(HttpMethod.Post, "api/servers/1/modules", new CreateServerModuleRequest());
    }

    [Fact]
    public async Task UpdateServerModuleAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ServerModuleDto { Id = 1 }) };
        Assert.NotNull(await api.Servers.UpdateServerModuleAsync(1, 1, new UpdateServerModuleRequest()));
        h.AssertSent(HttpMethod.Put, "api/servers/1/modules/1", new UpdateServerModuleRequest());
    }

    [Fact]
    public async Task DeleteServerModuleAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Servers.DeleteServerModuleAsync(1, 1));
        h.AssertSent(HttpMethod.Delete, "api/servers/1/modules/1");
    }

    // --- Server Apps ---

    [Fact]
    public async Task GetServerAppsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ServerAppDto> { Items = [], TotalCount = 0 })
        };
        Assert.Empty(await api.Servers.GetServerAppsAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/servers/1/apps?page=1&pageSize=100&sortDescending=false");
    }

    [Fact]
    public async Task CreateServerAppAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ServerAppDto { Id = 1 }) };
        Assert.NotNull(await api.Servers.CreateServerAppAsync(1, new CreateServerAppRequest()));
        h.AssertSent(HttpMethod.Post, "api/servers/1/apps", new CreateServerAppRequest());
    }

    [Fact]
    public async Task CreateServerAppAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Servers.CreateServerAppAsync(1, new CreateServerAppRequest()));
        h.AssertSent(HttpMethod.Post, "api/servers/1/apps", new CreateServerAppRequest());
    }

    [Fact]
    public async Task UpdateServerAppAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ServerAppDto { Id = 1 }) };
        Assert.NotNull(await api.Servers.UpdateServerAppAsync(1, 1, new UpdateServerAppRequest()));
        h.AssertSent(HttpMethod.Put, "api/servers/1/apps/1", new UpdateServerAppRequest());
    }

    [Fact]
    public async Task DeleteServerAppAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Servers.DeleteServerAppAsync(1, 1));
        h.AssertSent(HttpMethod.Delete, "api/servers/1/apps/1");
    }

    // --- Project Sub-Resources ---

    [Fact]
    public async Task GetProjectServersAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ProjectServerDto> { Items = [], TotalCount = 0 })
        };
        Assert.Empty(await api.Projects.GetProjectServersAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/projects/1/servers?page=1&pageSize=100&sortDescending=false");
    }

    [Fact]
    public async Task GetProjectTasksAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ServerTaskDto> { Items = [], TotalCount = 0 })
        };
        Assert.Empty((await api.Projects.GetProjectTasksAsync(1, ct: Xunit.TestContext.Current.CancellationToken)).Items);
        h.AssertSent(HttpMethod.Get, "api/projects/1/tasks?page=1&pageSize=25&sortDescending=false");
    }

    [Fact]
    public async Task GetProjectLogsAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<TaskLogDto> { Items = [], TotalCount = 0 })
        };
        Assert.Empty((await api.Projects.GetProjectLogsAsync(1, ct: Xunit.TestContext.Current.CancellationToken)).Items);
        h.AssertSent(HttpMethod.Get, "api/projects/1/logs?page=1&pageSize=50&sortDescending=false");
    }

    [Fact]
    public async Task GetProjectActivityAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<ProjectActivityDto>()) };
        Assert.Empty(await api.Projects.GetProjectActivityAsync(1));
        h.AssertSent(HttpMethod.Get, "api/projects/1/activity?count=20");
    }

    // --- Logs (unmasked) ---

    [Fact]
    public async Task GetTaskLogsUnmaskedAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<TaskLogDto>()) };
        Assert.Empty(await api.Monitoring.GetTaskLogsUnmaskedAsync(5, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/logs/task/5/unmasked");
    }

    // --- Audit ---

    [Fact]
    public async Task GetAuditLogsAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<AuditLogDto> { Items = [], TotalCount = 0 })
        };
        var result = await api.Monitoring.GetAuditLogsAsync(search: "test", action: "Create");
        Assert.Empty(result.Items);
        Assert.Contains("search=test", h.LastRequest!.RequestUri!.ToString());
        h.AssertSent(HttpMethod.Get, "api/audit?page=1&pageSize=50&search=test&action=Create");
    }

    [Fact]
    public async Task GetAuditActionsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<string> { "Create", "Delete" }) };
        Assert.Equal(2, (await api.Monitoring.GetAuditActionsAsync()).Count);
        h.AssertSent(HttpMethod.Get, "api/audit/actions");
    }

    [Fact]
    public async Task GetAuditEntityTypesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<string> { "Server" }) };
        Assert.Single(await api.Monitoring.GetAuditEntityTypesAsync());
        h.AssertSent(HttpMethod.Get, "api/audit/entity-types");
    }

    // --- Apache ---

    [Fact]
    public async Task GetApacheStateAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ApacheDataDto()) };
        Assert.NotNull(await api.ServerTools.GetApacheStateAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/servers/1/apache");
    }

    [Fact]
    public async Task GetApacheModulesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<ApacheModuleDto>()) };
        Assert.Empty(await api.ServerTools.GetApacheModulesAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/apache/modules");
    }

    [Fact]
    public async Task GetApacheVirtualHostsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<ApacheVirtualHostDto>()) };
        Assert.Empty(await api.ServerTools.GetApacheVirtualHostsAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/apache/vhosts");
    }

    [Fact]
    public async Task ExecuteApacheActionAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.ExecuteApacheActionAsync(1, new ApacheActionRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/apache/action", new ApacheActionRequest());
    }

    [Fact]
    public async Task GetApacheLogsAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.GetApacheLogsAsync(1, new ApacheLogRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/apache/logs", new ApacheLogRequest());
    }

    [Fact]
    public async Task GetApacheVHostConfigAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.GetApacheVHostConfigAsync(1, "site1", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/servers/1/apache/vhosts/site1/config");
    }

    [Fact]
    public async Task SaveApacheVHostConfigAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.SaveApacheVHostConfigAsync(1, new ApacheVHostSaveRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/servers/1/apache/vhosts//config", new ApacheVHostSaveRequest());
    }

    [Fact]
    public async Task GetApacheHtaccessAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.GetApacheHtaccessAsync(1, "/var/www", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/servers/1/apache/htaccess?documentRoot=%2Fvar%2Fwww");
    }

    [Fact]
    public async Task SaveApacheHtaccessAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.SaveApacheHtaccessAsync(1, new ApacheHtaccessSaveRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/servers/1/apache/htaccess", new ApacheHtaccessSaveRequest());
    }

    // --- Certbot ---

    [Fact]
    public async Task GetCertbotCertificatesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<CertbotCertificateDto>()) };
        Assert.Empty(await api.ServerTools.GetCertbotCertificatesAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/certbot");
    }

    [Fact]
    public async Task ExecuteCertbotActionAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.ExecuteCertbotActionAsync(1, new CertbotActionRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/certbot/action", new CertbotActionRequest());
    }

    [Fact]
    public async Task CreateCertbotCertificateAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.ServerTools.CreateCertbotCertificateAsync(1, new CertbotCreateRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/certbot/create", new CertbotCreateRequest());
    }

    // --- Module Links ---

    [Fact]
    public async Task GetModuleLinksAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<ModuleLinkDto>()) };
        Assert.Empty(await api.Servers.GetModuleLinksAsync(1));
        h.AssertSent(HttpMethod.Get, "api/servers/1/module-links");
    }

    [Fact]
    public async Task GetLinksForResourceAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<LinkedResourceDto>()) };
        Assert.Empty(await api.Servers.GetLinksForResourceAsync(1, ModuleLinkType.Docker, "nginx"));
        h.AssertSent(HttpMethod.Get, "api/servers/1/module-links/resource?sourceType=Docker&sourceIdentifier=nginx");
    }

    [Fact]
    public async Task GetModuleLinksPageAsync_PostsResourceSetAndReturnsPage()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<LinkedResourceDto>
            {
                Items = [new LinkedResourceDto { LinkId = 5, Identifier = "site.conf" }],
                TotalCount = 26,
                Page = 2,
                PageSize = 25
            })
        };
        var request = new ModuleLinkPageRequest
        {
            SourceType = ModuleLinkType.Docker,
            ResourceIdentifiers = ["nginx", "worker"],
            Page = 2,
            PageSize = 25,
            Search = "site"
        };

        var result = await api.Servers.GetModuleLinksPageAsync(1, request, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(26, result.TotalCount);
        Assert.Equal("site.conf", Assert.Single(result.Items).Identifier);
        Assert.Equal(HttpMethod.Post, h.LastRequest!.Method);
        Assert.EndsWith("api/servers/1/module-links/resource-page", h.LastRequest.RequestUri!.ToString());
        var sent = await h.LastRequest.Content!.ReadFromJsonAsync<ModuleLinkPageRequest>(Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(sent);
        Assert.Equal(["nginx", "worker"], sent.ResourceIdentifiers);
        Assert.Equal(2, sent.Page);
        h.AssertSent(HttpMethod.Post, "api/servers/1/module-links/resource-page", json: """{"sourceType":0,"resourceIdentifiers":["nginx","worker"],"page":2,"pageSize":25,"search":"site","sortBy":null,"sortDescending":false,"filters":null,"sorts":null}""");
    }

    [Fact]
    public async Task CreateModuleLinkAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ModuleLinkDto { Id = 1 }) };
        Assert.NotNull(await api.Servers.CreateModuleLinkAsync(1, new CreateModuleLinkRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/module-links", new CreateModuleLinkRequest());
    }

    [Fact]
    public async Task CreateModuleLinkAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Servers.CreateModuleLinkAsync(1, new CreateModuleLinkRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/module-links", new CreateModuleLinkRequest());
    }

    [Fact]
    public async Task DeleteModuleLinkAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Servers.DeleteModuleLinkAsync(1, 5, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/servers/1/module-links/5");
    }

    [Fact]
    public async Task AutoDetectModuleLinksAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Servers.AutoDetectModuleLinksAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/servers/1/module-links/auto-detect");
    }

    // --- Variable Libraries ---

    [Fact]
    public async Task GetVariableLibrariesAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<VariableLibraryDto> { Items = [], TotalCount = 0 })
        };
        Assert.Empty((await api.Variables.GetVariableLibrariesAsync()).Items);
        h.AssertSent(HttpMethod.Get, "api/variable-libraries?page=1&pageSize=25");
    }

    [Fact]
    public async Task GetVariableLibraryDetailAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VariableLibraryDetailDto { Id = 1 }) };
        Assert.NotNull(await api.Variables.GetVariableLibraryDetailAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/variable-libraries/1");
    }

    [Fact]
    public async Task GetVariableLibraryNamesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<string> { "lib1" }) };
        Assert.Single(await api.Variables.GetVariableLibraryNamesAsync());
        h.AssertSent(HttpMethod.Get, "api/variable-libraries/names");
    }

    [Fact]
    public async Task GetVariableSuggestionKeysAsync_UsesAggregatePagedEndpoint()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<string>
            {
                Items = ["KEY"],
                TotalCount = 201,
                Page = 2,
                PageSize = 200
            })
        };

        var result = await api.Variables.GetVariableSuggestionKeysAsync(2, 200, 7, ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(["KEY"], result.Items);
        var uri = h.LastRequest!.RequestUri!.ToString();
        Assert.Contains("api/variable-libraries/suggestion-keys", uri);
        Assert.Contains("page=2", uri);
        Assert.Contains("pageSize=200", uri);
        Assert.Contains("projectId=7", uri);
        h.AssertSent(HttpMethod.Get, "api/variable-libraries/suggestion-keys?page=2&pageSize=200&sortBy=Key&sortDescending=false&projectId=7");
    }

    [Fact]
    public async Task CreateVariableLibraryAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VariableLibraryDto { Id = 1 }) };
        Assert.NotNull(await api.Variables.CreateVariableLibraryAsync(new CreateVariableLibraryRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/variable-libraries", new CreateVariableLibraryRequest());
    }

    [Fact]
    public async Task CreateVariableLibraryAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Variables.CreateVariableLibraryAsync(new CreateVariableLibraryRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/variable-libraries", new CreateVariableLibraryRequest());
    }

    [Fact]
    public async Task UpdateVariableLibraryAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VariableLibraryDto { Id = 1 }) };
        Assert.NotNull(await api.Variables.UpdateVariableLibraryAsync(1, new UpdateVariableLibraryRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/variable-libraries/1", new UpdateVariableLibraryRequest());
    }

    [Fact]
    public async Task DeleteVariableLibraryAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Variables.DeleteVariableLibraryAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/variable-libraries/1");
    }

    [Fact]
    public async Task CreateVariableEntryAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VariableEntryDto { Id = 1 }) };
        Assert.True((await api.Variables.CreateVariableEntryAsync(1, new CreateVariableEntryRequest(), Xunit.TestContext.Current.CancellationToken)).IsSuccess);
        h.AssertSent(HttpMethod.Post, "api/variable-libraries/1/entries", new CreateVariableEntryRequest());
    }

    [Fact]
    public async Task CreateVariableEntryAsync_ExistingKey_IsARefusalTheCallerExplains()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json(new ApiError { Message = "The key 'A' already exists in this library." }) };
        var outcome = await api.Variables.CreateVariableEntryAsync(1, new CreateVariableEntryRequest { Key = "A" }, Xunit.TestContext.Current.CancellationToken);
        Assert.False(outcome.IsSuccess);
        Assert.Equal(HttpStatusCode.BadRequest, outcome.StatusCode);
        h.AssertSent(HttpMethod.Post, "api/variable-libraries/1/entries", json: """{"value":"","key":"A"}""");
    }

    [Fact]
    public async Task UpdateVariableEntryAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VariableEntryDto { Id = 1 }) };
        Assert.True((await api.Variables.UpdateVariableEntryAsync(1, 1, new UpdateVariableEntryRequest(), Xunit.TestContext.Current.CancellationToken)).IsSuccess);
        h.AssertSent(HttpMethod.Put, "api/variable-libraries/1/entries/1", new UpdateVariableEntryRequest());
    }

    [Fact]
    public async Task DeleteVariableEntryAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Variables.DeleteVariableEntryAsync(1, 1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/variable-libraries/1/entries/1");
    }

    [Fact]
    public async Task GetVariableEntryVersionsPageAsync_ReturnsPage()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<VariableEntryVersionDto> { TotalCount = 51 })
        };
        var result = await api.Variables.GetVariableEntryVersionsPageAsync(
            1, 2, 2, 25, sortBy: "Version", sortDescending: true,
            ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(51, result.TotalCount);
        Assert.Contains("api/variable-libraries/1/entries/2/versions", h.LastRequest!.RequestUri!.ToString());
        Assert.Contains("page=2", h.LastRequest.RequestUri.ToString());
        Assert.Contains("sortDescending=true", h.LastRequest.RequestUri.ToString());
        h.AssertSent(HttpMethod.Get, "api/variable-libraries/1/entries/2/versions?page=2&pageSize=25&sortBy=Version&sortDescending=true");
    }

    [Fact]
    public async Task ExportVariableEntriesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<VariableEntryDto>()) };
        Assert.Empty(await api.Variables.ExportVariableEntriesAsync(1));
        h.AssertSent(HttpMethod.Get, "api/variable-libraries/1/export");
    }

    [Fact]
    public async Task ImportVariableEntriesAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ImportResultDto()) };
        Assert.NotNull(await api.Variables.ImportVariableEntriesAsync(1, [], Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/variable-libraries/1/import", json: "[]");
    }

    [Fact]
    public async Task ImportVariableEntriesAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Variables.ImportVariableEntriesAsync(1, [], Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/variable-libraries/1/import", json: """[]""");
    }

    // --- Vaults ---

    [Fact]
    public async Task GetVaultsAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<VaultDto> { Items = [], TotalCount = 0 })
        };
        Assert.Empty((await api.Variables.GetVaultsAsync()).Items);
        h.AssertSent(HttpMethod.Get, "api/vaults?page=1&pageSize=25");
    }

    [Fact]
    public async Task GetVaultDetailAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VaultDetailDto { Id = 1 }) };
        Assert.NotNull(await api.Variables.GetVaultDetailAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/vaults/1");
    }

    [Fact]
    public async Task GetVaultNamesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<string> { "vault1" }) };
        Assert.Single(await api.Variables.GetVaultNamesAsync());
        h.AssertSent(HttpMethod.Get, "api/vaults/names");
    }

    [Fact]
    public async Task CreateVaultAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VaultDto { Id = 1 }) };
        Assert.NotNull(await api.Variables.CreateVaultAsync(new CreateVaultRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/vaults", new CreateVaultRequest());
    }

    [Fact]
    public async Task CreateVaultAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Variables.CreateVaultAsync(new CreateVaultRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/vaults", new CreateVaultRequest());
    }

    [Fact]
    public async Task UpdateVaultAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VaultDto { Id = 1 }) };
        Assert.NotNull(await api.Variables.UpdateVaultAsync(1, new UpdateVaultRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/vaults/1", new UpdateVaultRequest());
    }

    [Fact]
    public async Task DeleteVaultAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Variables.DeleteVaultAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/vaults/1");
    }

    [Fact]
    public async Task CreateVaultSecretAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VaultSecretDto { Key = "k" }) };
        Assert.NotNull(await api.Variables.CreateVaultSecretAsync(1, new CreateVaultSecretRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/vaults/1/secrets", new CreateVaultSecretRequest());
    }

    [Fact]
    public async Task UpdateVaultSecretAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new VaultSecretDto { Key = "k" }) };
        Assert.NotNull(await api.Variables.UpdateVaultSecretAsync(1, 1, new UpdateVaultSecretRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/vaults/1/secrets/1", new UpdateVaultSecretRequest());
    }

    [Fact]
    public async Task DeleteVaultSecretAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Variables.DeleteVaultSecretAsync(1, 1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/vaults/1/secrets/1");
    }

    [Fact]
    public async Task GetVaultSecretVersionsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<VaultSecretVersionDto>()) };
        Assert.Empty(await api.Variables.GetVaultSecretVersionsAsync(1, 1));
        h.AssertSent(HttpMethod.Get, "api/vaults/1/secrets/1/versions");
    }

    [Fact]
    public async Task ExportVaultSecretKeysAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<string> { "secret-key" }) };
        Assert.Single(await api.Variables.ExportVaultSecretKeysAsync(1));
        h.AssertSent(HttpMethod.Get, "api/vaults/1/export-keys");
    }

    [Fact]
    public async Task ImportVaultSecretsAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ImportResultDto()) };
        Assert.NotNull(await api.Variables.ImportVaultSecretsAsync(1, [], Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/vaults/1/import", json: "[]");
    }

    // --- Pipeline Templates ---

    [Fact]
    public async Task DryRunPipelineAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new DryRunResultDto()) };
        Assert.NotNull(await api.Pipelines.DryRunPipelineAsync(1, ct: Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/pipelines/1/dry-run", json: "null");
    }

    [Fact]
    public async Task DryRunPipelineAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Pipelines.DryRunPipelineAsync(1, ct: Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/pipelines/1/dry-run", json: """null""");
    }

    [Fact]
    public async Task GetPipelineTemplatesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new List<PipelineTemplateSummaryDto>())
        };
        Assert.Empty(await api.PipelineTemplates.GetPipelineTemplatesAsync());
        h.AssertSent(HttpMethod.Get, "api/pipelines/templates");
    }

    [Fact]
    public async Task GetPipelineTemplatesAsync_CachesResult()
    {
        var (api, h) = Create();
        var callCount = 0;
        h.ResponseFactory = _ => { callCount++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<PipelineTemplateSummaryDto>()) }; };

        await api.PipelineTemplates.GetPipelineTemplatesAsync();
        await api.PipelineTemplates.GetPipelineTemplatesAsync();
        Assert.Equal(1, callCount);
        h.AssertSent(HttpMethod.Get, "api/pipelines/templates");
    }

    [Fact]
    public async Task InvalidateTemplateCache_ClearsCachedTemplates()
    {
        var (api, h) = Create();
        var callCount = 0;
        h.ResponseFactory = _ => { callCount++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<PipelineTemplateSummaryDto>()) }; };

        await api.PipelineTemplates.GetPipelineTemplatesAsync();
        api.PipelineTemplates.InvalidateTemplateCache();
        await api.PipelineTemplates.GetPipelineTemplatesAsync();
        Assert.Equal(2, callCount);
        Assert.Equal(["http://test/api/pipelines/templates", "http://test/api/pipelines/templates"], h.RequestUris);
        Assert.All(h.RequestBodies, body => Assert.Null(body));
    }

    [Fact]
    public async Task GetPipelineTemplateAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineTemplateDto { Id = 1 }) };
        Assert.NotNull(await api.PipelineTemplates.GetPipelineTemplateAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/pipelines/templates/1");
    }

    [Fact]
    public async Task GetPipelineTemplateVersionsAsync_UsesPagedMetadataEndpoint()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<PipelineTemplateVersionSummaryDto>
            {
                Items = [new PipelineTemplateVersionSummaryDto { Version = 11 }],
                TotalCount = 21
            })
        };

        var result = await api.PipelineTemplates.GetPipelineTemplateVersionsAsync(
            1, 2, 10, "Version", true, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(21, result.TotalCount);
        var uri = h.LastRequest!.RequestUri!.ToString();
        Assert.Contains("api/pipelines/templates/1/versions", uri);
        Assert.Contains("page=2", uri);
        Assert.Contains("sortDescending=true", uri);
        h.AssertSent(HttpMethod.Get, "api/pipelines/templates/1/versions?page=2&pageSize=10&sortBy=Version&sortDescending=true");
    }

    [Fact]
    public async Task GetPipelineTemplateVersionAsync_LoadsOnlyRequestedYaml()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PipelineTemplateVersionDto { Version = 7, YamlContent = "name: seven" })
        };

        var result = await api.PipelineTemplates.GetPipelineTemplateVersionAsync(1, 7, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("name: seven", result!.YamlContent);
        Assert.EndsWith("api/pipelines/templates/1/versions/7", h.LastRequest!.RequestUri!.ToString());
        h.AssertSent(HttpMethod.Get, "api/pipelines/templates/1/versions/7");
    }

    [Fact]
    public async Task ResolvePipelineTemplateAsync_ReadsPlainYamlResponse()
    {
        var (api, h) = Create();
        const string yaml = "name: portfolio-release-fast\nextends: portfolio-release-fast-template@1";
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(yaml, System.Text.Encoding.UTF8, "text/plain")
        };

        var result = await api.PipelineTemplates.ResolvePipelineTemplateAsync(12, 1, ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(yaml, result);
        Assert.EndsWith("api/pipelines/templates/12/resolve?version=1", h.LastRequest!.RequestUri!.ToString());
        h.AssertSent(HttpMethod.Post, "api/pipelines/templates/12/resolve?version=1", json: """null""");
    }

    [Fact]
    public async Task CreatePipelineTemplateAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineTemplateDto { Id = 1 }) };
        Assert.NotNull(await api.PipelineTemplates.CreatePipelineTemplateAsync(new CreatePipelineTemplateRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/pipelines/templates", new CreatePipelineTemplateRequest());
    }

    [Fact]
    public async Task UpdatePipelineTemplateAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineTemplateDto { Id = 1 }) };
        Assert.NotNull(await api.PipelineTemplates.UpdatePipelineTemplateAsync(1, new UpdatePipelineTemplateRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/pipelines/templates/1", new UpdatePipelineTemplateRequest());
    }

    [Fact]
    public async Task DeletePipelineTemplateAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.PipelineTemplates.DeletePipelineTemplateAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/pipelines/templates/1");
    }

    [Fact]
    public async Task ExportPipelineTemplateAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        var result = await api.PipelineTemplates.ExportPipelineTemplateAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Length);
        h.AssertSent(HttpMethod.Get, "api/pipelines/templates/1/export");
    }

    [Fact]
    public async Task ExportPipelineTemplateAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await api.PipelineTemplates.ExportPipelineTemplateAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/pipelines/templates/1/export");
    }

    [Fact]
    public async Task ImportPipelineTemplateAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineTemplateDto { Id = 2 }) };
        Assert.NotNull(await api.PipelineTemplates.ImportPipelineTemplateAsync([1, 2], "template.json", Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Post, h.LastRequest!.Method);
        Assert.Equal("http://test/api/pipelines/templates/import", h.RequestUris.Single());
        Assert.StartsWith("multipart/form-data", h.LastRequest.Content!.Headers.ContentType!.MediaType, StringComparison.Ordinal);
        Assert.Contains("name=file", h.RequestBodies[^1], StringComparison.Ordinal);
        Assert.Contains("filename=template.json", h.RequestBodies[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidatePipelineYamlAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PipelineYamlDefinition()) };
        Assert.NotNull(await api.Packages.ValidatePipelineYamlAsync("stages: []", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/pipelines/validate", json: """{"yaml":"stages: []","organizationId":null}""");
    }

    [Fact]
    public async Task ValidatePipelineYamlAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Packages.ValidatePipelineYamlAsync("bad yaml", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/pipelines/validate", json: """{"yaml":"bad yaml","organizationId":null}""");
    }

    // --- Releases ---

    [Fact]
    public async Task GetReleasesAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<ReleaseDto> { Items = [], TotalCount = 0 })
        };
        Assert.Empty((await api.Projects.GetReleasesAsync()).Items);
        h.AssertSent(HttpMethod.Get, "api/releases?page=1&pageSize=25");
    }

    [Fact]
    public async Task GetReleaseAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ReleaseDto { Id = 1 }) };
        Assert.NotNull(await api.Projects.GetReleaseAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/releases/1");
    }

    [Fact]
    public async Task SyncReleasesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<ReleaseDto>()) };
        Assert.Empty(await api.Projects.SyncReleasesAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/releases/sync/1");
    }

    [Fact]
    public async Task TriggerReleaseBuildAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ReleaseDto { Id = 1 }) };
        Assert.NotNull(await api.Projects.TriggerReleaseBuildAsync(1, new TriggerReleaseBuildRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/releases/1/build", new TriggerReleaseBuildRequest());
    }

    [Fact]
    public async Task TriggerReleaseBuildAsync_Failure()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Projects.TriggerReleaseBuildAsync(1, new TriggerReleaseBuildRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/releases/1/build", new TriggerReleaseBuildRequest());
    }

    [Fact]
    public async Task RollbackReleaseAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ReleaseDto { Id = 1 }) };
        Assert.NotNull(await api.Projects.RollbackReleaseAsync(1, new RollbackReleaseRequest { PipelineId = 1 }, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/releases/1/rollback", json: """{"pipelineId":1,"restoreDatabase":false,"backupRunId":null}""");
    }

    [Fact]
    public async Task PromoteReleaseAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new ReleaseDto { Id = 1 }) };
        Assert.NotNull(await api.Projects.PromoteReleaseAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/releases/1/promote");
    }

    // --- Admin: Users ---

    [Fact]
    public async Task GetUsersAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<UserDto> { Items = [], TotalCount = 0 })
        };
        Assert.Empty((await api.Auth.GetUsersAsync(cancellationToken: TestContext.Current.CancellationToken)).Items);
        h.AssertSent(HttpMethod.Get, "api/users?page=1&pageSize=25");
    }

    [Fact]
    public async Task GetUserDetailAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new UserDto { Id = 1 }) };
        Assert.NotNull(await api.Auth.GetUserDetailAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/users/1");
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new UserDto { Id = 1 }) };
        Assert.NotNull(await api.Auth.GetCurrentUserAsync(Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/users/me");
    }

    [Fact]
    public async Task CreateUserAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new UserDto { Id = 1 }) };
        var outcome = await api.Auth.CreateUserAsync(new CreateUserRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(outcome.Value);
        Assert.Null(outcome.Error);
        h.AssertSent(HttpMethod.Post, "api/users", new CreateUserRequest());
    }

    [Fact]
    public async Task CreateUserAsync_Conflict_PreservesApiError()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = Json(new ApiError { Message = "Username already exists." })
        };

        var outcome = await api.Auth.CreateUserAsync(new CreateUserRequest(), Xunit.TestContext.Current.CancellationToken);

        Assert.Null(outcome.Value);
        Assert.Equal(HttpStatusCode.Conflict, outcome.StatusCode);
        Assert.Equal("Username already exists.", outcome.Error?.Message);
        h.AssertSent(HttpMethod.Post, "api/users", new CreateUserRequest());
    }

    [Fact]
    public async Task UpdateUserAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new UserDto { Id = 1 }) };
        Assert.NotNull(await api.Auth.UpdateUserAsync(1, new UpdateUserRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/users/1", new UpdateUserRequest());
    }

    [Fact]
    public async Task DeleteUserAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Auth.DeleteUserAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/users/1");
    }

    [Fact]
    public async Task ChangeUserPasswordAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Auth.ChangeUserPasswordAsync(1, new ChangeUserPasswordRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/users/1/change-password", new ChangeUserPasswordRequest());
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.True(await api.Auth.ChangeOwnPasswordAsync(new ChangeUserPasswordRequest { CurrentPassword = "old", NewPassword = "newpass123" }, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/users/me/change-password", json: """{"currentPassword":"old","newPassword":"newpass123"}""");
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_BadRequest_ReturnsFalse()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.False(await api.Auth.ChangeOwnPasswordAsync(new ChangeUserPasswordRequest { CurrentPassword = "wrong", NewPassword = "newpass123" }, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/users/me/change-password", json: """{"currentPassword":"wrong","newPassword":"newpass123"}""");
    }

    [Fact]
    public async Task GetRolesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<string> { "Admin" }) };
        Assert.Single(await api.Auth.GetRolesAsync());
        h.AssertSent(HttpMethod.Get, "api/users/roles");
    }

    [Fact]
    public async Task GetRoleDtosAsync_ReturnsPaginatedResultAndBuildsQuery()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<RoleDto>
            {
                Items = [new RoleDto { Id = 1, Name = "Admin" }],
                TotalCount = 26,
                Page = 2,
                PageSize = 25
            })
        };

        var result = await api.Auth.GetRoleDtosAsync(2, 25, "adm", "Name", true);

        Assert.Single(result.Items);
        Assert.Equal(26, result.TotalCount);
        var uri = h.LastRequest!.RequestUri!.ToString();
        Assert.Contains("page=2", uri);
        Assert.Contains("pageSize=25", uri);
        Assert.Contains("search=adm", uri);
        Assert.Contains("sortBy=Name", uri);
        Assert.Contains("sortDescending=true", uri);
        h.AssertSent(HttpMethod.Get, "api/roles?page=2&pageSize=25&search=adm&sortBy=Name&sortDescending=true");
    }

    [Fact]
    public async Task GetRoleAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new RoleDto { Id = 1, Name = "Admin" }) };
        var result = await api.Auth.GetRoleAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Get, "api/roles/1");
    }

    [Fact]
    public async Task GetRoleUsersAsync_BuildsServerPaginationQuery()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<RoleUserDto>())
        };

        await api.Auth.GetRoleUsersAsync(4, 3, 25, "ali", "Username", true, Xunit.TestContext.Current.CancellationToken);

        var uri = h.LastRequest!.RequestUri!.ToString();
        Assert.Contains("api/roles/4/users", uri);
        Assert.Contains("page=3", uri);
        Assert.Contains("pageSize=25", uri);
        Assert.Contains("search=ali", uri);
        Assert.Contains("sortBy=Username", uri);
        Assert.Contains("sortDescending=true", uri);
        h.AssertSent(HttpMethod.Get, "api/roles/4/users?page=3&pageSize=25&search=ali&sortBy=Username&sortDescending=true");
    }

    [Fact]
    public async Task GetUsersAvailableForRoleAsync_UsesDedicatedPagedEndpoint()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<RoleUserDto>())
        };

        await api.Auth.GetUsersAvailableForRoleAsync(
            4, 2, 10, "bob", "Username", ct: Xunit.TestContext.Current.CancellationToken);

        var uri = h.LastRequest!.RequestUri!.ToString();
        Assert.Contains("api/roles/4/available-users", uri);
        Assert.Contains("page=2", uri);
        Assert.Contains("pageSize=10", uri);
        Assert.Contains("search=bob", uri);
        Assert.DoesNotContain("api/users?pageSize=200", uri);
        h.AssertSent(HttpMethod.Get, "api/roles/4/available-users?page=2&pageSize=10&search=bob&sortBy=Username&sortDescending=false");
    }

    [Fact]
    public async Task CreateRoleAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new RoleDto { Id = 1, Name = "New" }) };
        var result = await api.Auth.CreateRoleAsync(new CreateRoleRequest { Name = "New" }, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Post, "api/roles", json: """{"name":"New","description":"","permissionKeys":null,"organizationId":null}""");
    }

    [Fact]
    public async Task CreateRoleAsync_Failure_ReturnsNull()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Auth.CreateRoleAsync(new CreateRoleRequest { Name = "x" }, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/roles", json: """{"name":"x","description":"","permissionKeys":null,"organizationId":null}""");
    }

    [Fact]
    public async Task UpdateRoleAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new RoleDto { Id = 1, Name = "Updated" }) };
        var result = await api.Auth.UpdateRoleAsync(1, new UpdateRoleRequest { Name = "Updated" }, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Put, "api/roles/1", json: """{"name":"Updated","description":""}""");
    }

    [Fact]
    public async Task DeleteRoleAsync_Success_ReturnsTrue()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Auth.DeleteRoleAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/roles/1");
    }

    [Fact]
    public async Task GetRolePermissionsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<ResourcePermissionDto>()) };
        var result = await api.Auth.GetRolePermissionsAsync(1);
        Assert.Empty(result);
        h.AssertSent(HttpMethod.Get, "api/roles/1/permissions");
    }

    [Fact]
    public async Task SetRolePermissionsAsync_Success_ReturnsTrue()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Auth.SetRolePermissionsAsync(1, new SetResourcePermissionsRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/roles/1/permissions", new SetResourcePermissionsRequest());
    }

    [Fact]
    public async Task CloneRoleAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new RoleDto { Id = 2, Name = "Clone" }) };
        var result = await api.Auth.CloneRoleAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Post, "api/roles/1/clone");
    }

    [Fact]
    public async Task CloneRoleAsync_Failure_ReturnsNull()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await api.Auth.CloneRoleAsync(99, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/roles/99/clone");
    }

    [Fact]
    public async Task GetUserEffectivePermissionsAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new UserPermissionSummaryDto()) };
        var result = await api.Auth.GetUserEffectivePermissionsAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Get, "api/users/1/effective-permissions");
    }

    [Fact]
    public async Task GetMyPermissionsAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new UserPermissionSummaryDto()) };
        var result = await api.Auth.GetMyPermissionsAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Get, "api/users/me/permissions");
    }

    // --- Admin: Dashboards ---

    [Fact]
    public async Task GetDashboardsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<DashboardDto>()) };
        Assert.Empty(await api.Monitoring.GetDashboardsAsync());
        h.AssertSent(HttpMethod.Get, "api/dashboards");
    }

    [Fact]
    public async Task GetAdminDashboardAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new DashboardDto { Id = 1 }) };
        Assert.NotNull(await api.Monitoring.GetDashboardAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/dashboards/1");
    }

    [Fact]
    public async Task CreateDashboardAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new DashboardDto { Id = 1 }) };
        Assert.NotNull(await api.Monitoring.CreateDashboardAsync(new CreateDashboardRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/dashboards", new CreateDashboardRequest());
    }

    [Fact]
    public async Task UpdateDashboardAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new DashboardDto { Id = 1 }) };
        Assert.NotNull(await api.Monitoring.UpdateDashboardAsync(1, new UpdateDashboardRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/dashboards/1", new UpdateDashboardRequest());
    }

    [Fact]
    public async Task DeleteDashboardAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Monitoring.DeleteDashboardAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/dashboards/1");
    }

    // --- Admin: Plugins ---

    [Fact]
    public async Task GetPluginsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<PluginRegistrationDto> { Items = [], TotalCount = 0 })
        };
        Assert.Empty(await api.Settings.GetPluginsAsync(Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/plugins?page=1&pageSize=100&sortDescending=false");
    }

    [Fact]
    public async Task GetPluginAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PluginRegistrationDto { Id = 1 }) };
        Assert.NotNull(await api.Settings.GetPluginAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/plugins/1");
    }

    [Fact]
    public async Task RegisterPluginAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PluginRegistrationDto { Id = 1 }) };
        Assert.NotNull(await api.Settings.RegisterPluginAsync(new RegisterPluginRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/plugins", new RegisterPluginRequest());
    }

    [Fact]
    public async Task UpdatePluginAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PluginRegistrationDto { Id = 1 }) };
        Assert.NotNull(await api.Settings.UpdatePluginAsync(1, new UpdatePluginRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/plugins/1", new UpdatePluginRequest());
    }

    [Fact]
    public async Task UnregisterPluginAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Settings.UnregisterPluginAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/plugins/1");
    }

    // --- Admin: Alerts ---

    [Fact]
    public async Task GetAlertRulesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new List<AlertRuleDto>()) };
        Assert.Empty(await api.Monitoring.GetAlertRulesAsync());
        h.AssertSent(HttpMethod.Get, "api/alerts");
    }

    [Fact]
    public async Task CreateAlertRuleAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new AlertRuleDto { Id = 1 }) };
        Assert.NotNull(await api.Monitoring.CreateAlertRuleAsync(new CreateAlertRuleRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/alerts", new CreateAlertRuleRequest());
    }

    [Fact]
    public async Task UpdateAlertRuleAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new AlertRuleDto { Id = 1 }) };
        Assert.NotNull(await api.Monitoring.UpdateAlertRuleAsync(1, new UpdateAlertRuleRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Put, "api/alerts/1", new UpdateAlertRuleRequest());
    }

    [Fact]
    public async Task DeleteAlertRuleAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Monitoring.DeleteAlertRuleAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/alerts/1");
    }

    // --- Admin: Notifications ---

    [Fact]
    public async Task GetNotificationChannelsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<NotificationChannelDto>())
        };
        Assert.Empty(await api.Monitoring.GetNotificationChannelsAsync());
        h.AssertSent(HttpMethod.Get, "api/notifications/channels?page=1&pageSize=100");
    }

    [Fact]
    public async Task CreateNotificationChannelAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new NotificationChannelDto { Id = 1 }) };
        Assert.NotNull(await api.Monitoring.CreateNotificationChannelAsync(new CreateNotificationChannelRequest(), Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/notifications/channels", new CreateNotificationChannelRequest());
    }

    [Fact]
    public async Task DeleteNotificationChannelAsync_Success()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Monitoring.DeleteNotificationChannelAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/notifications/channels/1");
    }

    // --- Git Light Repos ---

    [Fact]
    public async Task GetGitReposAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<GitLightRepoDto>
            {
                Items = [new GitLightRepoDto { Id = 1, Name = "repo" }],
                TotalCount = 1
            })
        };
        var result = await api.Git.GetGitReposAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Single(result);
        h.AssertSent(HttpMethod.Get, "api/git/repos?page=1&pageSize=100&projectId=1&sortDescending=False");
    }

    [Fact]
    public async Task GetGitRepoAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new GitLightRepoDto { Id = 1, Name = "repo" })
        };
        var result = await api.Git.GetGitRepoAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("repo", result!.Name);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1");
    }

    [Fact]
    public async Task CreateGitRepoAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new GitLightRepoDto { Id = 1, Name = "new" })
        };
        var result = await api.Git.CreateGitRepoAsync(new CreateGitLightRepoRequest { Name = "new" }, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Post, "api/git/repos", json: """{"projectId":0,"name":"new","description":null,"defaultBranch":null}""");
    }

    [Fact]
    public async Task CreateGitRepoAsync_Failure_ReturnsNull()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        var result = await api.Git.CreateGitRepoAsync(new CreateGitLightRepoRequest { Name = "x" }, Xunit.TestContext.Current.CancellationToken);
        Assert.Null(result);
        h.AssertSent(HttpMethod.Post, "api/git/repos", json: """{"projectId":0,"name":"x","description":null,"defaultBranch":null}""");
    }

    [Fact]
    public async Task UpdateGitRepoAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new GitLightRepoDto { Id = 1, Name = "updated" })
        };
        var result = await api.Git.UpdateGitRepoAsync(1, new UpdateGitLightRepoRequest { Description = "updated" }, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Put, "api/git/repos/1", json: """{"description":"updated","defaultBranch":null,"isConfiguration":null}""");
    }

    [Fact]
    public async Task UpdateGitRepoAsync_Failure_ReturnsNull()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        var result = await api.Git.UpdateGitRepoAsync(1, new UpdateGitLightRepoRequest { Description = "x" }, Xunit.TestContext.Current.CancellationToken);
        Assert.Null(result);
        h.AssertSent(HttpMethod.Put, "api/git/repos/1", json: """{"description":"x","defaultBranch":null,"isConfiguration":null}""");
    }

    [Fact]
    public async Task DeleteGitRepoAsync_Success_ReturnsTrue()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Git.DeleteGitRepoAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/git/repos/1");
    }

    [Fact]
    public async Task DeleteGitRepoAsync_Failure_ReturnsFalse()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.False(await api.Git.DeleteGitRepoAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/git/repos/1");
    }

    // --- Git Light Branches ---

    [Fact]
    public async Task GetGitBranchesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<GitLightBranchDto>
            {
                Items = [new GitLightBranchDto { Name = "main" }],
                TotalCount = 1
            })
        };
        var result = await api.Git.GetGitBranchesAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Single(result);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/branches?page=1&pageSize=100&sortDescending=false");
    }

    [Fact]
    public async Task CreateGitBranchAsync_Success_ReturnsTrue()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Git.CreateGitBranchAsync(1, new CreateGitLightBranchRequest { Name = "feat", StartRef = "main" }, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/git/repos/1/branches", json: """{"name":"feat","startRef":"main"}""");
    }

    [Fact]
    public async Task DeleteGitBranchAsync_Success_ReturnsTrue()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Git.DeleteGitBranchAsync(1, "feat", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/git/repos/1/branches/feat");
    }

    // --- Git Light Tags ---

    [Fact]
    public async Task GetGitTagsAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<GitLightTagDto>
            {
                Items = [new GitLightTagDto { Name = "v1.0" }],
                TotalCount = 1
            })
        };
        var result = await api.Git.GetGitTagsAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Single(result);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/tags?page=1&pageSize=100&sortDescending=false");
    }

    [Fact]
    public async Task CreateGitTagAsync_Success_ReturnsTrue()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Git.CreateGitTagAsync(1, new CreateGitLightTagRequest { Name = "v1.0", Ref = "main" }, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/git/repos/1/tags", json: """{"name":"v1.0","ref":"main","message":null}""");
    }

    [Fact]
    public async Task DeleteGitTagAsync_Success_ReturnsTrue()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Git.DeleteGitTagAsync(1, "v1.0", Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/git/repos/1/tags/v1.0");
    }

    // --- Git Light File Browser ---

    [Fact]
    public async Task GetGitTreeAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<GitLightTreeEntryDto>
            {
                Items = [new GitLightTreeEntryDto { Name = "file.txt" }],
                TotalCount = 1
            })
        };
        var result = await api.Git.GetGitTreeAsync(1, "main", "src", Xunit.TestContext.Current.CancellationToken);
        Assert.Single(result);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/tree?page=1&pageSize=100&sortDescending=false&ref=main&path=src");
    }

    [Fact]
    public async Task GetGitBlobAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new GitLightBlobDto { Content = "hello" })
        };
        var result = await api.Git.GetGitBlobAsync(1, "main", "file.txt", Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/blob?ref=main&path=file.txt");
    }

    [Fact]
    public async Task GetGitBlameAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new List<GitLightBlameLine> { new() { LineNumber = 1 } })
        };
        var result = await api.Git.GetGitBlameAsync(1, "main", "file.txt");
        Assert.Single(result);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/blame?ref=main&path=file.txt");
    }

    // --- Git Light Commits ---

    [Fact]
    public async Task GetGitCommitsAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<GitLightCommitDto> { Items = [new() { Sha = "abc" }], TotalCount = 1 })
        };
        var result = await api.Git.GetGitCommitsAsync(1, "main", 1, 10, "search");
        Assert.Single(result.Items);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/commits?page=1&pageSize=10&ref=main&search=search");
    }

    // --- Git Light Pull Requests ---

    [Fact]
    public async Task GetGitPullRequestsAsync_ReturnsPaginated()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<InternalPullRequestDto> { Items = [new() { Number = 1 }], TotalCount = 1 })
        };
        var result = await api.Git.GetGitPullRequestsAsync(1, status: PullRequestStatus.Open);
        Assert.Single(result.Items);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/pull-requests?page=1&pageSize=25&status=Open");
    }

    [Fact]
    public async Task GetGitPullRequestAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new InternalPullRequestDto { Number = 1 }) };
        var result = await api.Git.GetGitPullRequestAsync(1, 1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/pull-requests/1");
    }

    [Fact]
    public async Task CreateGitPullRequestAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new InternalPullRequestDto { Number = 1 }) };
        var result = await api.Git.CreateGitPullRequestAsync(1, new CreateInternalPullRequestRequest { Title = "PR" }, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Post, "api/git/repos/1/pull-requests", json: """{"sourceBranch":"","targetBranch":"","title":"PR","description":null}""");
    }

    [Fact]
    public async Task CreateGitPullRequestAsync_Failure_ReturnsNull()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        Assert.Null(await api.Git.CreateGitPullRequestAsync(1, new CreateInternalPullRequestRequest { Title = "x" }, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/git/repos/1/pull-requests", json: """{"sourceBranch":"","targetBranch":"","title":"x","description":null}""");
    }

    [Fact]
    public async Task MergeGitPullRequestAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new InternalPullRequestDto { Number = 1 }) };
        var result = await api.Git.MergeGitPullRequestAsync(1, 1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Post, "api/git/repos/1/pull-requests/1/merge");
    }

    [Fact]
    public async Task MergeGitPullRequestAsync_Failure_ReturnsNull()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.Conflict);
        Assert.Null(await api.Git.MergeGitPullRequestAsync(1, 1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Post, "api/git/repos/1/pull-requests/1/merge");
    }

    [Fact]
    public async Task CloseGitPullRequestAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new InternalPullRequestDto { Number = 1 }) };
        var result = await api.Git.CloseGitPullRequestAsync(1, 1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Post, "api/git/repos/1/pull-requests/1/close");
    }

    [Fact]
    public async Task GetGitPullRequestDiffAsync_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new PullRequestDiffDto()) };
        var result = await api.Git.GetGitPullRequestDiffAsync(1, 1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/pull-requests/1/diff");
    }

    // --- Git Branch Protection ---

    [Fact]
    public async Task GetGitCommitGraphAsync_ReturnsString()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("graph-data") };
        var result = await api.Git.GetGitCommitGraphAsync(1, ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("graph-data", result);
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/graph?maxCount=100");
    }

    [Fact]
    public async Task GetGitBranchProtectionRulesAsync_ReturnsList()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(new PaginatedResult<BranchProtectionRuleDto>
            {
                Items = [new BranchProtectionRuleDto { Id = 1 }],
                TotalCount = 1
            })
        };
        Assert.Single(await api.Git.GetGitBranchProtectionRulesAsync(1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Get, "api/git/repos/1/branch-protection?page=1&pageSize=100&sortDescending=false");
    }

    [Fact]
    public async Task CreateGitBranchProtectionRuleAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new BranchProtectionRuleDto { Id = 1 }) };
        var result = await api.Git.CreateGitBranchProtectionRuleAsync(1, new CreateBranchProtectionRuleRequest { Pattern = "main" }, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Post, "api/git/repos/1/branch-protection", json: """{"pattern":"main","preventDeletion":true,"preventForcePush":true,"protectionLevel":0}""");
    }

    [Fact]
    public async Task UpdateGitBranchProtectionRuleAsync_Success_ReturnsDto()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new BranchProtectionRuleDto { Id = 1 }) };
        var result = await api.Git.UpdateGitBranchProtectionRuleAsync(1, 1, new UpdateBranchProtectionRuleRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        h.AssertSent(HttpMethod.Put, "api/git/repos/1/branch-protection/1", new UpdateBranchProtectionRuleRequest());
    }

    [Fact]
    public async Task DeleteGitBranchProtectionRuleAsync_Success_ReturnsTrue()
    {
        var (api, h) = Create();
        h.Response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True(await api.Git.DeleteGitBranchProtectionRuleAsync(1, 1, Xunit.TestContext.Current.CancellationToken));
        h.AssertSent(HttpMethod.Delete, "api/git/repos/1/branch-protection/1");
    }

    [Fact]
    public async Task GetAllProjectsAsync_AggregatesEveryServerPage()
    {
        var (api, h) = Create();
        h.ResponseFactory = request =>
        {
            var secondPage = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal);
            var items = secondPage
                ? Enumerable.Range(101, 5).Select(id => new ProjectDto { Id = id, Name = $"Project {id}" }).ToList()
                : Enumerable.Range(1, 100).Select(id => new ProjectDto { Id = id, Name = $"Project {id}" }).ToList();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json(new PaginatedResult<ProjectDto> { Items = items, TotalCount = 105 })
            };
        };

        var result = await api.Projects.GetAllProjectsAsync(ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(105, result.Count);
        Assert.Contains(h.RequestUris, uri => uri.Contains("page=2", StringComparison.Ordinal));
        Assert.Equal(["http://test/api/projects?page=1&pageSize=200", "http://test/api/projects?page=2&pageSize=200"], h.RequestUris);
    }

    [Fact]
    public async Task GetAllServersAsync_AggregatesEveryServerPage()
    {
        var (api, h) = Create();
        h.ResponseFactory = request =>
        {
            var secondPage = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal);
            var items = secondPage
                ? Enumerable.Range(101, 5).Select(id => new ServerDto { Id = id, Name = $"Server {id}" }).ToList()
                : Enumerable.Range(1, 100).Select(id => new ServerDto { Id = id, Name = $"Server {id}" }).ToList();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json(new PaginatedResult<ServerDto> { Items = items, TotalCount = 105 })
            };
        };

        var result = await api.Servers.GetAllServersAsync();

        Assert.Equal(105, result.Count);
        Assert.Contains(h.RequestUris, uri => uri.Contains("page=2", StringComparison.Ordinal));
        Assert.Equal(["http://test/api/servers?page=1&pageSize=100", "http://test/api/servers?page=2&pageSize=100"], h.RequestUris);
    }

    [Fact]
    public async Task GetAllVariableLibrariesAsync_AggregatesEveryServerPage()
    {
        var (api, h) = Create();
        h.ResponseFactory = request =>
        {
            var secondPage = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal);
            var items = secondPage
                ? Enumerable.Range(101, 5).Select(id => new VariableLibraryDto { Id = id, Name = $"Library {id}" }).ToList()
                : Enumerable.Range(1, 100).Select(id => new VariableLibraryDto { Id = id, Name = $"Library {id}" }).ToList();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json(new PaginatedResult<VariableLibraryDto> { Items = items, TotalCount = 105 })
            };
        };

        var result = await api.Variables.GetAllVariableLibrariesAsync();

        Assert.Equal(105, result.Count);
        Assert.Contains(h.RequestUris, uri => uri.Contains("page=2", StringComparison.Ordinal));
        Assert.Equal(["http://test/api/variable-libraries?page=1&pageSize=100", "http://test/api/variable-libraries?page=2&pageSize=100"], h.RequestUris);
    }

    /// <summary>A manual run posts a dictionary whose only entry is the 32-hex idempotency key.</summary>
    private static void AssertTriggerRunSent(TestHandler h, string relativeUrl)
    {
        Assert.Equal(HttpMethod.Post, h.LastRequest!.Method);
        Assert.Equal($"http://test/{relativeUrl}", h.RequestUris.Single());
        var body = JsonSerializer.Deserialize<Dictionary<string, string>>(h.RequestBodies[^1]!);
        var entry = Assert.Single(body!);
        Assert.Equal("AETHEUS_RUN_IDEMPOTENCY_KEY", entry.Key);
        Assert.Matches("^[0-9a-f]{32}$", entry.Value);
    }

    // --- Test Handler ---

    private sealed class TestHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public HttpRequestMessage? LastRequest { get; private set; }
        public List<string> RequestUris { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        /// <summary>The body of every request, read while the request is still alive.</summary>
        public List<string?> RequestBodies { get; } = [];

        /// <summary>Asserts the last request: exact verb, exact relative URI and JSON body (none when omitted).</summary>
        public void AssertSent(HttpMethod method, string relativeUrl, object? body = null, string? json = null) =>
            SentRequestAssert.Matches(
                LastRequest!.Method.Method, RequestUris[^1], RequestBodies[^1], method, relativeUrl, body, json);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            if (ResponseFactory is not null)
                return ResponseFactory(request);
            return Response;
        }
    }
}

