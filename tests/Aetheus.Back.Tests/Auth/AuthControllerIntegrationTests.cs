// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.Tests;

public class AuthControllerIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _anonClient;

    public AuthControllerIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomWebApplicationFactory.GenerateTestToken());
        _anonClient = _factory.CreateClient();
    }

    // ──── Login ────

    [Fact]
    public async Task PublicDemoInfo_DefaultEnvironment_IsDisabled()
    {
        var response = await _anonClient.GetAsync(
            "/api/auth/public-demo", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PublicDemoInfoDto>(
            TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.False(result.Enabled);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsToken()
    {
        var response = await _anonClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "admin@aetheus.local",
            Password = "admin"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.True(result.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Login_InvalidCredentials_Returns401()
    {
        var response = await _anonClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "admin@aetheus.local",
            Password = "wrong-password"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_TheUsernameOfTheSeededAdmin_IsRefusedLikeAWrongPassword()
    {
        // "admin" is the seeded administrator's username and "admin" its password in this factory: the
        // username typed in the e-mail field is no sign-in, whatever the password.
        var byUsername = await _anonClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "admin",
            Password = "admin"
        }, cancellationToken: TestContext.Current.CancellationToken);
        var wrongPassword = await _anonClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "admin@aetheus.local",
            Password = "wrong-password"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, byUsername.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await byUsername.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Login_TheFormerUsernameField_IsNoSignInAndIssuesNoToken()
    {
        var response = await _anonClient.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("\"token\"",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_EmptyBody_Returns400()
    {
        var response = await _anonClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ──── Registration Tokens ────

    [Fact]
    public async Task CreateRegistrationToken_ValidRequest_ReturnsToken()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/registration-tokens",
            new CreateRegistrationTokenRequest { ExpirationHours = 24 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<RegistrationTokenDto>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(token);
        Assert.False(string.IsNullOrEmpty(token.Token));
        Assert.False(token.IsUsed);
        Assert.True(token.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task GetRegistrationTokens_Authenticated_ReturnsTokenList()
    {
        await _client.PostAsJsonAsync("/api/auth/registration-tokens",
            new CreateRegistrationTokenRequest { ExpirationHours = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        var response = await _client.GetAsync("/api/auth/registration-tokens", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<List<RegistrationTokenDto>>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tokens);
    }

    [Fact]
    public async Task GetRegistrationTokens_Unauthenticated_Returns401()
    {
        var response = await _anonClient.GetAsync("/api/auth/registration-tokens", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateRegistrationToken_Unauthenticated_Returns401()
    {
        var response = await _anonClient.PostAsJsonAsync("/api/auth/registration-tokens",
            new CreateRegistrationTokenRequest { ExpirationHours = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ──── Server Registration ────

    [Fact]
    public async Task RegisterServer_ValidToken_ReturnsServerIdAndBearer()
    {
        var regToken = await CreateRegistrationTokenAsync();

        var response = await _anonClient.PostAsJsonAsync("/api/auth/register", new ServerRegistrationRequest
        {
            RegistrationToken = regToken,
            Hostname = "test-host",
            OsDescription = "Linux 6.0",
            AgentVersion = "1.0.0",
            IpAddress = "10.0.0.1"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ServerRegistrationResponse>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.True(result.ServerId > 0);
        Assert.False(string.IsNullOrEmpty(result.BearerToken));
    }

    [Fact]
    public async Task RegisterServer_InvalidToken_Returns400()
    {
        var response = await _anonClient.PostAsJsonAsync("/api/auth/register", new ServerRegistrationRequest
        {
            RegistrationToken = "invalid-token-that-does-not-exist",
            Hostname = "test-host",
            OsDescription = "Linux",
            AgentVersion = "1.0.0",
            IpAddress = "10.0.0.1"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterServer_ExpiredToken_Returns400()
    {
        var expiredToken = await SeedExpiredRegistrationTokenAsync();

        var response = await _anonClient.PostAsJsonAsync("/api/auth/register", new ServerRegistrationRequest
        {
            RegistrationToken = expiredToken,
            Hostname = "test-host",
            OsDescription = "Linux",
            AgentVersion = "1.0.0",
            IpAddress = "10.0.0.1"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterServer_UsedToken_Returns400()
    {
        var token = await CreateRegistrationTokenAsync();

        await _anonClient.PostAsJsonAsync("/api/auth/register", new ServerRegistrationRequest
        {
            RegistrationToken = token,
            Hostname = "host-1",
            OsDescription = "Linux",
            AgentVersion = "1.0.0",
            IpAddress = "10.0.0.1"
        }, cancellationToken: TestContext.Current.CancellationToken);

        var response = await _anonClient.PostAsJsonAsync("/api/auth/register", new ServerRegistrationRequest
        {
            RegistrationToken = token,
            Hostname = "host-2",
            OsDescription = "Linux",
            AgentVersion = "1.0.0",
            IpAddress = "10.0.0.2"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<string> CreateRegistrationTokenAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/registration-tokens",
            new CreateRegistrationTokenRequest { ExpirationHours = 24 });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<RegistrationTokenDto>(TestJsonOptions.Default);
        return token!.Token;
    }

    private async Task<string> SeedExpiredRegistrationTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var token = new RegistrationToken
        {
            Token = $"expired-token-{Guid.NewGuid()}",
            IsUsed = false,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            ExpiresAt = DateTime.UtcNow.AddDays(-1)
        };
        db.RegistrationTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Token;
    }
}
