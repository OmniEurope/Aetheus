// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Shared.Components.Auth;
using Aetheus.Shared.Components.Users;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-18 and R-25 on the real host and PostgreSQL: the Users grid shows when an account last
/// authenticated (set at login, empty before), and an administrator cannot switch off their own
/// account from the grid toggle (the server refuses it, not only the page).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UserLastActivityIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task LastActivityAt_IsEmptyBeforeTheFirstLogin_AndSetByIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var user = await CreateUserAsync(factory, admin, "activity");

        Assert.Null((await ReadAsync(admin, user.Id, ct)).LastActivityAt);

        var before = DateTime.UtcNow.AddMinutes(-1);
        using var _ = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var after = await ReadAsync(admin, user.Id, ct);
        Assert.NotNull(after.LastActivityAt);
        Assert.True(after.LastActivityAt >= before);
    }

    [Fact]
    public async Task AnAdministrator_CannotDeactivateTheirOwnAccount_ButCanDeactivateAnotherUser()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var bootstrap = await IntegrationUsers.AdminClientAsync(factory);
        var self = await CreateUserAsync(factory, bootstrap, "selfoff", RoleNames.Admin);
        var other = await CreateUserAsync(factory, bootstrap, "otheroff");
        using var asSelf = await IntegrationUsers.LoginClientAsync(factory, self.Username);

        var refused = await asSelf.PutAsJsonAsync($"/api/users/{self.Id}", Deactivation(self), ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True((await ReadAsync(bootstrap, self.Id, ct)).IsActive);

        var allowed = await asSelf.PutAsJsonAsync($"/api/users/{other.Id}", Deactivation(other), ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.False((await ReadAsync(bootstrap, other.Id, ct)).IsActive);
    }

    private static UpdateUserRequest Deactivation(UserDto user) => new()
    {
        Username = user.Username,
        Email = user.Email,
        IsActive = false,
        Roles = user.Roles
    };

    private static async Task<UserDto> ReadAsync(HttpClient client, int id, CancellationToken ct)
    {
        var page = await client.GetFromJsonAsync<PaginatedResult<UserDto>>(
            "/api/users?page=1&pageSize=200", IntegrationJsonOptions.Default, ct);
        return page!.Items.Single(user => user.Id == id);
    }

    private static async Task<UserDto> CreateUserAsync(AetheusWebApplicationFactory factory, HttpClient admin, string prefix, params string[] roles)
    {
        var ct = TestContext.Current.CancellationToken;
        var username = $"{prefix}-{Guid.NewGuid():N}"[..40];
        var created = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = username,
            // PLAN-003 P3-2: the e-mail is the required, unique sign-in identifier.
            Email = $"{username}@example.com",
            Roles = [.. roles]
        }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct))!;
        await IntegrationUsers.SetPasswordAsync(factory.Services, user.Id, IntegrationUsers.Password);
        return user;
    }
}
