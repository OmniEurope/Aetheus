// SPDX-License-Identifier: EUPL-1.2
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Configuration;
using Aetheus.Back.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-1 (owner decision 2026-10-29), through real HTTP and PostgreSQL: a fresh install seeds its administrator with
/// the default address and the configured password, and arms its first step. Until its holder sets up their own
/// name, address and password, that account reaches nothing else (not even the plain password change, nor Git);
/// afterwards the seeded credentials open nothing, and the step is audited.
/// </summary>
public sealed class SeededAdministratorSetupIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string SeededPassword = AetheusWebApplicationFactory.AdminPassword;
    private const string OwnPassword = "Own-Administrator-Pwd-2026!";
    private const string OwnEmail = "owner@example.com";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The integration host runs in Development, whose configuration turns the step off for the shared dev seed;
    // this host turns it back on, as every non-development install has it.
    private static WebApplicationFactory<Program> WithSetupRequired(AetheusWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting(SeededAdministratorSetup.ConfigurationKey, "true"));

    [Fact]
    public async Task FreshSeed_CreatesTheAdministrator_WithTheDefaultAddress_AndArmsItsSetup()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = WithSetupRequired(factory);
        using var client = host.CreateClient();

        var login = await LoginAsync(client, SeededAdminEmail.Default, SeededPassword);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = (await login.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, Ct))!;
        Assert.True(body.MustChangePassword);
        Assert.True(body.MustSetUpAccount);
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(body.Token).Claims.ToList();
        Assert.Contains(claims, claim => claim.Type == AetheusClaimTypes.MustChangePassword && claim.Value == "true");
        Assert.Contains(claims, claim => claim.Type == AetheusClaimTypes.MustSetUpAccount && claim.Value == "true");

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await db.Users.AsNoTracking().SingleAsync(user => user.Username == SeededAdminEmail.SeededUsername, Ct);
        Assert.Equal(SeededAdminEmail.Default, admin.Email);
        Assert.True(admin.MustSetUpAccount);
        Assert.True(admin.MustChangePassword);
    }

    [Fact]
    public async Task BeforeTheSetup_TheSeededAdministrator_ReachesOnlyTheFirstStepEndpoints()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = WithSetupRequired(factory);
        using var client = host.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginByEmailAsync(client, SeededAdminEmail.Default, SeededPassword, Ct));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me", Ct)).StatusCode);
        foreach (var path in new[] { "/api/servers", "/api/users", "/api/projects", "/api/settings" })
        {
            var refused = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.True(refused.Headers.Contains(ForcedPasswordChangeMiddleware.RequiredHeader), path);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = "second-admin",
            Email = "second-admin@example.com",
            Roles = ["Admin"]
        }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/personal-access-tokens",
            new CreatePersonalAccessTokenRequest { Name = "before-setup", Scope = PatScope.ReadOnly, ExpirationDays = 30 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/hubs/servers/negotiate?negotiateVersion=1", null, Ct)).StatusCode);

        // The plain password change, which the gate lets through, would keep the public default address: refused.
        var passwordOnly = await client.PostAsJsonAsync("/api/users/me/change-password",
            new ChangeUserPasswordRequest { CurrentPassword = SeededPassword, NewPassword = OwnPassword }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, passwordOnly.StatusCode);

        // Git over HTTP reads no token, so it refuses the seeded credentials itself.
        using var scope = host.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        Assert.Null(await auth.AuthenticateBasicAsync(SeededAdminEmail.Default, SeededPassword, Ct));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True((await db.Users.AsNoTracking().SingleAsync(user => user.Username == SeededAdminEmail.SeededUsername, Ct)).MustSetUpAccount);
    }

    [Fact]
    public async Task AfterTheSetup_TheSeededCredentialsOpenNothing_AndTheOwnAccountIsAFullAdministrator()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = WithSetupRequired(factory);
        using var client = host.CreateClient();
        var seededToken = await IntegrationAuth.LoginByEmailAsync(client, SeededAdminEmail.Default, SeededPassword, Ct);
        IntegrationAuth.SetBearer(client, seededToken);

        // Each refusal leaves the account as it was.
        Assert.Equal(HttpStatusCode.BadRequest, (await SetUpAsync(client, SeededAdminEmail.Default, SeededPassword, OwnPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetUpAsync(client, OwnEmail, "Not-The-Installation-2026!", OwnPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetUpAsync(client, OwnEmail, SeededPassword, SeededPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SetUpAsync(client, OwnEmail, SeededPassword, OwnPassword)).StatusCode);

        // Every session of the seeded credentials ended with them.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users/me", Ct)).StatusCode);
        IntegrationAuth.SetBearer(client, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, SeededAdminEmail.Default, SeededPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, SeededAdminEmail.Default, OwnPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, OwnEmail, SeededPassword)).StatusCode);

        var own = await LoginAsync(client, OwnEmail, OwnPassword);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var ownBody = (await own.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, Ct))!;
        Assert.False(ownBody.MustChangePassword);
        Assert.False(ownBody.MustSetUpAccount);
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(ownBody.Token).Claims.ToList();
        Assert.DoesNotContain(claims, claim => claim.Type is AetheusClaimTypes.MustChangePassword or AetheusClaimTypes.MustSetUpAccount);
        Assert.Contains(claims, claim => claim.Value == "Admin");
        IntegrationAuth.SetBearer(client, ownBody.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/servers", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users", Ct)).StatusCode);

        // The step is done for good: it cannot be replayed to take the account over again.
        Assert.Equal(HttpStatusCode.BadRequest, (await SetUpAsync(client, "other@example.com", OwnPassword, "Another-Pwd-2026-Long!")).StatusCode);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Users.AsNoTracking().SingleAsync(user => user.Email == OwnEmail, Ct);
        Assert.Equal("owner", account.Username);
        Assert.False(account.MustSetUpAccount);
        Assert.False(account.MustChangePassword);
        Assert.False(await db.Users.AsNoTracking().AnyAsync(user => user.Email == SeededAdminEmail.Default, Ct));
        Assert.Equal(1, await db.AuditLogs.AsNoTracking().CountAsync(
            log => log.Action == "AccountSetUp" && log.EntityType == "User" && log.EntityId == account.Id, Ct));
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        Assert.Null(await auth.AuthenticateBasicAsync(SeededAdminEmail.Default, SeededPassword, Ct));
        Assert.Equal("owner", await auth.AuthenticateBasicAsync(OwnEmail, OwnPassword, Ct));
    }

    private static Task<HttpResponseMessage> SetUpAsync(HttpClient client, string email, string currentPassword, string newPassword) =>
        client.PostAsJsonAsync("/api/users/me/account-setup", new SetUpOwnAccountRequest
        {
            Username = "Owner",
            Email = email,
            CurrentPassword = currentPassword,
            NewPassword = newPassword
        }, Ct);

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password }, Ct);
}
