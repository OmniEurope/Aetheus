// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Creates non-admin users carrying exactly the rights a test grants, so an HTTP assertion proves the
/// real authorization path (role permissions, organization membership) instead of the Admin fast path.
/// </summary>
internal static class IntegrationUsers
{
    public const string Password = "Integration-User-Pwd-2026!";

    /// <summary>One permission the custom role grants; a null <paramref name="ResourceId"/> is global.</summary>
    public sealed record Grant(ResourceType ResourceType, int? ResourceId, Permission Permission);

    /// <summary>
    /// Inserts a user with its own role holding only <paramref name="grants"/> (no grant at all is a valid,
    /// permission-less role), optionally a member of <paramref name="organizationId"/>, and returns its
    /// id and username.
    /// </summary>
    public static async Task<(int Id, string Username)> CreateAsync(
        IServiceProvider services, string prefix, IReadOnlyCollection<Grant> grants, int? organizationId = null)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var role = new Role { Name = $"{prefix}-role-{Guid.NewGuid():N}", Description = "Integration test role" };
        db.Roles.Add(role);
        var username = $"{prefix}-{Guid.NewGuid():N}";
        var user = new User
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        foreach (var grant in grants)
        {
            db.ResourcePermissions.Add(new ResourcePermission
            {
                RoleId = role.Id,
                ResourceType = grant.ResourceType,
                ResourceId = grant.ResourceId,
                Permission = grant.Permission
            });
        }
        if (organizationId is { } orgId)
        {
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = orgId,
                UserId = user.Id,
                Role = OrganizationRole.Member
            });
        }
        await db.SaveChangesAsync(ct);
        return (user.Id, username);
    }

    /// <summary>A client of <paramref name="factory"/> logged in over HTTP as <paramref name="username"/>.</summary>
    public static async Task<HttpClient> LoginClientAsync(WebApplicationFactory<Program> factory, string username)
    {
        var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsync(
            client, username, Password, TestContext.Current.CancellationToken));
        return client;
    }

    /// <summary>A client of <paramref name="factory"/> logged in over HTTP as the seeded bootstrap admin.</summary>
    public static async Task<HttpClient> AdminClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(
            client, TestContext.Current.CancellationToken));
        return client;
    }

    /// <summary>
    /// The <c>id</c> of every row of a list response: a bare JSON array or a paginated
    /// <c>{ "items": [...] }</c> envelope.
    /// </summary>
    public static async Task<List<int>> ReadListIdsAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.GetProperty("items");
        return rows.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToList();
    }
}
