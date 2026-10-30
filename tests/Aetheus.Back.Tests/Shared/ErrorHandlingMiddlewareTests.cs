// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aetheus.Back.Tests;

public class ErrorHandlingMiddlewareTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = CreateAuthenticatedClient(factory);

    [Fact]
    public async Task GetServer_NonExistent_Returns404()
    {
        var response = await _client.GetAsync("/api/servers/99999", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteServer_NonExistent_Returns404()
    {
        var response = await _client.DeleteAsync("/api/servers/99999", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPipeline_NonExistent_Returns404()
    {
        var response = await _client.GetAsync("/api/pipelines/99999", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Login_InvalidCredentials_Returns401WithApiError()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = "wrong@example.com", Password = "wrong" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiError>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.False(string.IsNullOrEmpty(error.Message));
    }

    [Fact]
    public async Task RegisterServer_InvalidToken_Returns400WithApiError()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new ServerRegistrationRequest
            {
                RegistrationToken = "invalid-token",
                Hostname = "test-host",
                OsDescription = "Test OS",
                AgentVersion = "1.0.0",
                IpAddress = "127.0.0.1"
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiError>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.Contains("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unauthenticated_Request_Returns401()
    {
        var unauthClient = factory.CreateClient();

        var response = await unauthClient.GetAsync("/api/servers", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidateYaml_InvalidYaml_Returns400WithApiError()
    {
        var response = await _client.PostAsJsonAsync("/api/pipelines/validate",
            new { Yaml = "not: valid: yaml: [" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetDashboard_Returns200()
    {
        var response = await _client.GetAsync("/api/monitoring/dashboard", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dashboard = await response.Content.ReadFromJsonAsync<DashboardOverviewDto>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(dashboard);
    }

    private static HttpClient CreateAuthenticatedClient(CustomWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomWebApplicationFactory.GenerateTestToken());
        return client;
    }
}
