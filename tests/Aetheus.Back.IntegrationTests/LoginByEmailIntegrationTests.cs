// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-003 P3-2: people sign in by e-mail address, through real HTTP and PostgreSQL. The bodies are
/// written as raw JSON (not the DTO) so the scenario reads the same against any version of the contract.
/// </summary>
public sealed class LoginByEmailIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string UserPassword = "Login-By-Email-Pwd-2026!";

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    [Trait("Category", "RollbackRelevant")]
    public async Task Login_ByEmail_AnyCase_Succeeds_AndAWrongPasswordStaysGeneric()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, ct));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await client.PostAsJsonAsync("/api/users", new
        {
            username = $"mail-{suffix}",
            email = $"  Mixed.Case-{suffix}@Example.COM ",
            organizationRoles = await IntegrationUsers.InDefaultOrganizationAsync(client, "Reader")
        }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdId = (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct))!.Id;
        await IntegrationUsers.SetPasswordAsync(factory.Services, createdId, UserPassword);
        IntegrationAuth.SetBearer(client, null);

        // Stored normalized, so any case of the address signs in.
        await using (var db = new AppDbContext(Options()))
            Assert.Equal($"mixed.case-{suffix}@example.com",
                (await db.Users.AsNoTracking().SingleAsync(u => u.Username == $"mail-{suffix}", ct)).Email);
        var upper = await client.PostAsJsonAsync("/api/auth/login",
            new { email = $"MIXED.CASE-{suffix}@EXAMPLE.COM", password = UserPassword }, ct);
        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        var login = await upper.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, ct);
        Assert.False(string.IsNullOrEmpty(login!.Token));

        // A wrong password and an unknown address answer the same generic refusal.
        var wrong = await client.PostAsJsonAsync("/api/auth/login",
            new { email = $"mixed.case-{suffix}@example.com", password = "not-the-password-2026" }, ct);
        var unknown = await client.PostAsJsonAsync("/api/auth/login",
            new { email = $"nobody-{suffix}@example.com", password = UserPassword }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(
            (await wrong.Content.ReadFromJsonAsync<ApiError>(IntegrationJsonOptions.Default, ct))!.Message,
            (await unknown.Content.ReadFromJsonAsync<ApiError>(IntegrationJsonOptions.Default, ct))!.Message);

        // The username typed into the e-mail field is not a way in.
        var byNameInEmail = await client.PostAsJsonAsync("/api/auth/login",
            new { email = $"mail-{suffix}", password = UserPassword }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, byNameInEmail.StatusCode);
    }

    /// <summary>Owner decision 2026-10-28: the username is a display name, never a sign-in. Signing in with
    /// the username of an existing account is refused with the answer of a wrong password (no account
    /// enumeration) and audited like any failed sign-in; the e-mail of the same account succeeds.</summary>
    [Fact]
    public async Task Login_ByUsername_IsRefusedLikeAWrongPassword_WhileTheEmailOfTheSameAccountSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, ct));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"name-only-{suffix}";
        var email = $"person-{suffix}@example.com";
        var created = await client.PostAsJsonAsync("/api/users", new
        {
            username,
            email,
            organizationRoles = await IntegrationUsers.InDefaultOrganizationAsync(client, "Reader")
        }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await IntegrationUsers.SetPasswordAsync(
            factory.Services, (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct))!.Id, UserPassword);
        IntegrationAuth.SetBearer(client, null);

        // The e-mail of the account signs in.
        var byEmail = await client.PostAsJsonAsync("/api/auth/login", new { email, password = UserPassword }, ct);
        Assert.Equal(HttpStatusCode.OK, byEmail.StatusCode);
        Assert.False(string.IsNullOrEmpty((await byEmail.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, ct))!.Token));

        // The username typed into the e-mail field, with the right password, answers exactly like a wrong password.
        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "not-the-password-2026" }, ct);
        var usernameInEmailField = await client.PostAsJsonAsync("/api/auth/login", new { email = username, password = UserPassword }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, usernameInEmailField.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(ct), await usernameInEmailField.Content.ReadAsStringAsync(ct));

        // The former username field is no sign-in either: no token is issued.
        var legacyPayload = await client.PostAsJsonAsync("/api/auth/login", new { username, password = UserPassword }, ct);
        Assert.False(legacyPayload.IsSuccessStatusCode, $"A username must not sign in, got {(int)legacyPayload.StatusCode}.");
        Assert.DoesNotContain("\"token\"", await legacyPayload.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);

        // The refused attempt by name is audited like any failed sign-in.
        await using var db = new AppDbContext(Options());
        Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(
            log => log.Action == "LoginFailed.InvalidCredentials" && log.Details!.Contains(username), ct));
    }

    [Fact]
    public async Task UniqueIndex_RefusesASecondRowWithTheSameEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var reset = new AppDbContext(Options()))
        {
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);
            await reset.Database.MigrateAsync(ct);
        }

        await using var db = new AppDbContext(Options());
        await InsertUserAsync(db, "index-one", "same@example.com", ct);
        var violation = await Assert.ThrowsAsync<PostgresException>(
            () => InsertUserAsync(db, "index-two", "same@example.com", ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);
        Assert.Equal("IX_Users_Email", violation.ConstraintName);
        // Since 20261028210000_RequireUserEmail no account is without one: a missing or blank address is refused.
        var missing = await Assert.ThrowsAsync<PostgresException>(() => InsertUserAsync(db, "no-email", null, ct));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, missing.SqlState);
        var blank = await Assert.ThrowsAsync<PostgresException>(() => InsertUserAsync(db, "blank-email", "  ", ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, blank.SqlState);
        Assert.Equal("CK_Users_Email_NotBlank", blank.ConstraintName);
    }

    private static Task<int> InsertUserAsync(AppDbContext db, string username, string? email, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Users" ("Username", "PasswordHash", "Email", "IsActive", "CreatedAt", "UpdatedAt",
                "MustChangePassword", "SecurityStamp", "FailedLoginCount", "TotpEnabled")
            VALUES (@username, 'x', @email, true, now(), now(), false, 'stamp', 0, false)
            """,
            [new NpgsqlParameter("username", username), new NpgsqlParameter("email", (object?)email ?? DBNull.Value)],
            ct);
}
