// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Exercises forced password change through real HTTP and PostgreSQL: the persisted flag
/// and JWT claim restrict access until the password changes. A subsequent login must clear both
/// restrictions.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ForcedPasswordChangeIntegrationTests(PostgresFixture fixture)
{
    private const string AdminPassword = AetheusWebApplicationFactory.AdminPassword;

    [Fact]
    public async Task FirstLogin_WithForcedChange_FlagsToken_ThenClearsAfterChange()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        // 1. As admin, create a user that must change its password on first login.
        var adminToken = await IntegrationAuth.LoginAsAdminAsync(client, TestContext.Current.CancellationToken);
        IntegrationAuth.SetBearer(client, adminToken);

        var username = $"mcp-user-{Guid.NewGuid():N}";
        const string initialPassword = "Forced-Change-Pwd-2026!";
        const string newPassword = "Chosen-New-Pwd-2026!";

        var createResp = await client.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = username,
            Password = initialPassword,
            Roles = ["Reader"],
            MustChangePassword = true
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        // 2. First login succeeds but is flagged: the response says MustChangePassword and the JWT
        //    carries the aetheus:mcp claim so the Front can force the change-password screen.
        IntegrationAuth.SetBearer(client, null);
        var firstLogin = await LoginAsync(client, username, initialPassword);
        Assert.True(firstLogin.MustChangePassword);
        Assert.False(string.IsNullOrEmpty(firstLogin.Token));
        Assert.True(TokenHasForcedChangeClaim(firstLogin.Token),
            "First-login token must carry the aetheus:mcp claim while MustChangePassword is set.");

        // 3. Change the password as that user (self-service requires the current password).
        IntegrationAuth.SetBearer(client, firstLogin.Token);
        var changeResp = await client.PostAsJsonAsync("/api/users/me/change-password", new ChangeUserPasswordRequest
        {
            CurrentPassword = initialPassword,
            NewPassword = newPassword
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, changeResp.StatusCode);

        // 4. Re-login with the new password: the forced-change signal is gone, on both the response
        //    flag and the freshly minted token (no aetheus:mcp claim).
        IntegrationAuth.SetBearer(client, null);
        var secondLogin = await LoginAsync(client, username, newPassword);
        Assert.False(secondLogin.MustChangePassword);
        Assert.False(string.IsNullOrEmpty(secondLogin.Token));
        Assert.False(TokenHasForcedChangeClaim(secondLogin.Token),
            "A token minted after the change must NOT carry the aetheus:mcp claim.");

        // 5. The old (now-stale) initial password no longer authenticates.
        var staleResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = initialPassword
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, staleResp.StatusCode);
    }

    // SITE-SEC-001: the server, not only the front, holds a forced change, and an admin reset ends the
    // personal access tokens the previous holder of the account could still use.
    [Fact]
    public async Task AdminReset_RevokesPats_AndTheFlaggedTokenReachesOnlyTheChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var admin = factory.CreateClient();
        IntegrationAuth.SetBearer(admin, await IntegrationAuth.LoginAsAdminAsync(admin, ct));

        var username = $"reset-user-{Guid.NewGuid():N}";
        const string password = "Before-Reset-Pwd-2026!";
        var created = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = username,
            Password = password,
            Roles = ["Reader"],
            MustChangePassword = false
        }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var userId = (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct))!.Id;

        // The user mints a personal access token, then an admin resets the password.
        using var user = factory.CreateClient();
        IntegrationAuth.SetBearer(user, (await LoginAsync(user, username, password)).Token);
        var patResponse = await user.PostAsJsonAsync("/api/personal-access-tokens",
            new CreatePersonalAccessTokenRequest { Name = "before-reset", Scope = PatScope.ReadOnly, ExpirationDays = 30 }, ct);
        patResponse.EnsureSuccessStatusCode();
        var pat = (await patResponse.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(IntegrationJsonOptions.Default, ct))!.PlaintextToken;
        using var patClient = factory.CreateClient();
        IntegrationAuth.SetBearer(patClient, pat);
        Assert.Equal(HttpStatusCode.OK, (await patClient.GetAsync("/api/users/me", ct)).StatusCode);

        const string resetPassword = "Admin-Reset-Pwd-2026!";
        var reset = await admin.PostAsJsonAsync($"/api/users/{userId}/change-password",
            new ChangeUserPasswordRequest { NewPassword = resetPassword }, ct);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await patClient.GetAsync("/api/users/me", ct)).StatusCode);

        // The reset re-arms the forced change: the new token reaches the change and nothing else.
        var flagged = await LoginAsync(user, username, resetPassword);
        Assert.True(flagged.MustChangePassword);
        IntegrationAuth.SetBearer(user, flagged.Token);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/users/me", ct)).StatusCode);
        var refused = await user.GetAsync("/api/servers", ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True(refused.Headers.Contains(ForcedPasswordChangeMiddleware.RequiredHeader));
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/personal-access-tokens",
            new CreatePersonalAccessTokenRequest { Name = "during-change", Scope = PatScope.ReadOnly, ExpirationDays = 30 }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/hubs/servers/negotiate?negotiateVersion=1", null, ct)).StatusCode);

        var change = await user.PostAsJsonAsync("/api/users/me/change-password",
            new ChangeUserPasswordRequest { CurrentPassword = resetPassword, NewPassword = "Own-Choice-Pwd-2026!" }, ct);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = password
        });
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default);
        return login!;
    }

    /// <summary>
    /// Decodes the JWT payload (base64url, no signature validation needed - we only inspect the
    /// claim set the server minted) and reports whether the forced-change claim is present.
    /// </summary>
    private static bool TokenHasForcedChangeClaim(string jwt)
    {
        var segments = jwt.Split('.');
        Assert.True(segments.Length == 3, "Malformed JWT - expected three dot-separated segments.");

        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(segments[1]));
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.TryGetProperty(AetheusClaimTypes.MustChangePassword, out _);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }
}
