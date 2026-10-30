// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Creates non-admin users carrying exactly the rights a test grants, so an HTTP assertion proves the
/// real authorization path (role permissions, organization membership) instead of the Admin fast path.
/// </summary>
internal static class IntegrationUsers
{
    public const string Password = "Integration-User-Pwd-2026!";

    /// <summary>The organization DbInitializer seeds; the admin's API puts unassigned resources there.</summary>
    public const string DefaultOrganizationSlug = "aetheus";

    /// <summary>One permission the custom role grants; a null <paramref name="ResourceId"/> is global.</summary>
    public sealed record Grant(ResourceType ResourceType, int? ResourceId, Permission Permission);

    /// <summary>
    /// Inserts a user with its own role holding only <paramref name="grants"/> (no grant at all is a valid,
    /// permission-less role) and returns its id and username. Under ADR-059 a role other than Admin is
    /// held for one organization: the user holds it for <paramref name="organizationId"/>, or for the
    /// seeded default organization (where the admin's API creates projects and servers) when none is
    /// named, and is a member of that organization. The grants reach only that organization's resources.
    /// </summary>
    /// <remarks>R-11: <paramref name="permissionKeys"/> are the named permissions (action keys) the role also
    /// holds, role-wide; none by default, so a role built from levels alone holds no action.</remarks>
    public static async Task<(int Id, string Username)> CreateAsync(
        IServiceProvider services, string prefix, IReadOnlyCollection<Grant> grants, int? organizationId = null,
        IReadOnlyCollection<string>? permissionKeys = null)
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
            Email = $"{username}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var heldFor = organizationId
            ?? await db.Organizations.Where(organization => organization.Slug == DefaultOrganizationSlug)
                .Select(organization => organization.Id).SingleAsync(ct);
        db.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = user.Id, OrganizationId = heldFor, RoleId = role.Id });
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
        foreach (var key in permissionKeys ?? [])
            db.RolePermissionGrants.Add(new RolePermissionGrant { RoleId = role.Id, PermissionKey = key });
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = heldFor,
            UserId = user.Id,
            Role = OrganizationRole.Member
        });
        await db.SaveChangesAsync(ct);
        return (user.Id, username);
    }

    /// <summary>
    /// <paramref name="role"/> held for the seeded default organization, read over HTTP by
    /// <paramref name="admin"/>: what a user created through <c>POST /api/users</c> sends since
    /// ADR-059, where only Admin is held site-wide.
    /// </summary>
    public static async Task<List<OrganizationRoleAssignmentDto>> InDefaultOrganizationAsync(HttpClient admin, string role)
    {
        var organizations = await admin.GetFromJsonAsync<PaginatedResult<OrganizationDto>>(
            "/api/organizations?page=1&pageSize=100", IntegrationJsonOptions.Default, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The organization list came back empty.");
        var organizationId = organizations.Items.Single(organization => organization.Slug == DefaultOrganizationSlug).Id;
        return [new OrganizationRoleAssignmentDto { OrganizationId = organizationId, Role = role }];
    }

    /// <summary>
    /// R-14: an account created through <c>POST /api/users</c> holds a password nobody knows (the welcome link
    /// is the only way in). A test that signs in as that account gives it a known password here, straight in
    /// the database, as the person would by following the link.
    /// </summary>
    public static async Task SetPasswordAsync(IServiceProvider services, int userId, string password, bool mustChangePassword = false)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
        user.MustChangePassword = mustChangePassword;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A client of <paramref name="factory"/> logged in over HTTP as the account named
    /// <paramref name="username"/>.</summary>
    public static async Task<HttpClient> LoginClientAsync(WebApplicationFactory<Program> factory, string username)
    {
        var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await LoginTokenAsync(factory, client, username));
        return client;
    }

    /// <summary>The JWT of the account named <paramref name="username"/>, signed in over HTTP by its e-mail
    /// address (the only sign-in). The account is identified by username in the test, which reads its e-mail
    /// from the database the way a person knows their own address.</summary>
    public static async Task<string> LoginTokenAsync(
        WebApplicationFactory<Program> factory, HttpClient client, string username, string password = Password)
    {
        var ct = TestContext.Current.CancellationToken;
        string? email;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            email = await db.Users.AsNoTracking().Where(u => u.Username == username).Select(u => u.Email).SingleAsync(ct);
        }
        return await IntegrationAuth.LoginByEmailAsync(
            client, email ?? throw new InvalidOperationException($"Account '{username}' has no e-mail address to sign in with."),
            password, ct);
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
