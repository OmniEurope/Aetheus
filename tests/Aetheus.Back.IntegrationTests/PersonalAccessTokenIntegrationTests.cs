// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// End-to-end exercise of the Personal Access Token auth path (ADR-024 4.5) through the REAL HTTP
/// pipeline backed by PostgreSQL: a PAT presented as <c>Authorization: Bearer aeth_pat_...</c> is
/// forwarded from the JWT scheme, resolves to the owner's live identity, and is bounded by scope,
/// RBAC, and revocation - none of which the InMemory unit suite can prove (no real forwarding,
/// middleware ordering, or relational revocation).
/// </summary>
public sealed class PersonalAccessTokenIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<(string Plaintext, int Id)> CreatePatAsync(HttpClient jwtClient, PatScope scope)
    {
        var resp = await jwtClient.PostAsJsonAsync("/api/personal-access-tokens",
            new CreatePersonalAccessTokenRequest { Name = $"itest-{scope}", Scope = scope, ExpirationDays = 30 });
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(IntegrationJsonOptions.Default);
        return (created!.PlaintextToken, created.Token.Id);
    }

    private async Task<(AetheusWebApplicationFactory Factory, HttpClient Jwt)> LoggedInAsync()
    {
        var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var jwt = factory.CreateClient();
        var token = await IntegrationAuth.LoginAsAdminAsync(jwt, TestContext.Current.CancellationToken);
        IntegrationAuth.SetBearer(jwt, token);
        return (factory, jwt);
    }

    [Fact]
    public async Task ReadOnlyPat_Reads200_ButWriteRefused403()
    {
        var (factory, jwt) = await LoggedInAsync();
        await using var _ = factory;

        var (pat, _) = await CreatePatAsync(jwt, PatScope.ReadOnly);
        using var patClient = factory.CreateClient();
        IntegrationAuth.SetBearer(patClient, pat);

        var read = await patClient.GetAsync("/api/servers", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); // PAT authenticated + read allowed

        var write = await patClient.DeleteAsync("/api/servers/2147483647", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode); // scope middleware blocks the mutation
        var body = await write.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("read-only", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pat_CannotMintTokens_NoLaundering()
    {
        var (factory, jwt) = await LoggedInAsync();
        await using var _ = factory;

        var (pat, _) = await CreatePatAsync(jwt, PatScope.ReadWrite);
        using var patClient = factory.CreateClient();
        IntegrationAuth.SetBearer(patClient, pat);

        var resp = await patClient.PostAsJsonAsync("/api/personal-access-tokens",
            new CreatePersonalAccessTokenRequest { Name = "laundered", Scope = PatScope.ReadWrite, ExpirationDays = 30 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    /// <summary>BACK-006: a PAT cannot be renewed into an interactive JWT and refresh token, which would
    /// outlive its expiry and revocation. The interactive session itself still renews.</summary>
    [Fact]
    public async Task Pat_CannotRenewIntoAnInteractiveSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, jwt) = await LoggedInAsync();
        await using var _ = factory;

        var (pat, _) = await CreatePatAsync(jwt, PatScope.ReadWrite);
        using var patClient = factory.CreateClient();
        IntegrationAuth.SetBearer(patClient, pat);

        var renew = await patClient.PostAsync("/api/auth/renew", null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, renew.StatusCode);
        var error = await renew.Content.ReadFromJsonAsync<ApiError>(IntegrationJsonOptions.Default, ct);
        Assert.Equal("Token not eligible for renewal.", error!.Message);

        var interactive = await jwt.PostAsync("/api/auth/renew", null, ct);
        Assert.Equal(HttpStatusCode.OK, interactive.StatusCode);
    }

    [Fact]
    public async Task RevokedPat_IsRejected401_Immediately()
    {
        var (factory, jwt) = await LoggedInAsync();
        await using var _ = factory;

        var (pat, id) = await CreatePatAsync(jwt, PatScope.ReadOnly);
        using var patClient = factory.CreateClient();
        IntegrationAuth.SetBearer(patClient, pat);

        Assert.Equal(HttpStatusCode.OK, (await patClient.GetAsync("/api/servers", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);

        var revoke = await jwt.DeleteAsync($"/api/personal-access-tokens/{id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var afterRevoke = await patClient.GetAsync("/api/servers", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }
}
