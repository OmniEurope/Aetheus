// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Negative RBAC sweep: a freshly created <c>Reader</c> must be refused write/admin operations
/// through the real <c>ResourceAuthorizationService</c> + controller permission checks. The
/// seeded Reader role grants global <c>Read</c> on every resource type (so reads succeed - that
/// is the positive control here) but no <c>Write</c>/<c>Admin</c>, so every mutation must come
/// back <c>403 Forbidden</c>. Without this, a regression that let a Reader mutate admin-gated
/// resources would sail through the admin-only sweeps unnoticed - the security core (org-scoped
/// RBAC) would be wired but untested for denial.
/// </summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class ApiRbacNegativeSmokeTests(ApiSmokeFixture fixture)
{
    [Fact]
    public async Task Reader_CanRead_PositiveControl()
    {
        using var reader = await fixture.CreateReaderClientAsync();

        // Establishes the contrast for the denial tests: the 403s below are about the *operation*
        // (write/admin), not a blanket lockout - the same Reader reads collections just fine.
        using var resp = await reader.GetAsync("/api/projects", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Reader_CreateProject_Returns403()
    {
        using var reader = await fixture.CreateReaderClientAsync();

        using var resp = await reader.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"reader-denied-{Guid.NewGuid():N}",
            Description = "must be forbidden"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Reader_CreateVault_Returns403()
    {
        using var reader = await fixture.CreateReaderClientAsync();

        using var resp = await reader.PostAsJsonAsync("/api/vaults", new CreateVaultRequest
        {
            Name = $"reader-vault-{Guid.NewGuid():N}",
            Description = "must be forbidden",
            ProjectId = fixture.ProjectId
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Reader_DeleteVault_Returns403_BeforeExistenceCheck()
    {
        using var reader = await fixture.CreateReaderClientAsync();

        // The Admin-permission check runs before the existence lookup, so even a non-existent id
        // is refused with 403 (not 404) - proving authorization gates ahead of resource binding.
        using var resp = await reader.DeleteAsync("/api/vaults/999999", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    /// <summary>
    /// Moved here from the former <c>ResourceAuthorizationIntegrationTests</c>, whose other case
    /// (Reader reads projects but cannot create one) is already covered above. A role granted
    /// <c>Read</c> on ONE project must reach that project and be refused the other: the permission
    /// check depends on the real relational joins (role, resource_permission, resource), which the
    /// InMemory unit suite cannot prove. Additive, per the fixture contract: GUID-suffixed rows,
    /// all removed at the end.
    /// </summary>
    [Fact]
    public async Task UserWithResourceScopedPermission_CanOnlyAccessGrantedResource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var admin = fixture.CreateAdminClient();
        var projectIds = new List<int>();
        int? roleId = null;
        int? userId = null;
        try
        {
            var allowed = await CreateProjectAsync(admin, $"Allowed-{Guid.NewGuid():N}", "User can see this", ct);
            projectIds.Add(allowed);
            var refused = await CreateProjectAsync(admin, $"Forbidden-{Guid.NewGuid():N}", "User cannot see this", ct);
            projectIds.Add(refused);

            const string password = "Scoped-Test-Pwd-2026!";
            string username;
            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // A custom role with Read permission only on the first project.
                var role = new Role { Name = $"ScopedReader-{Guid.NewGuid():N}", Description = "read one project" };
                db.Roles.Add(role);
                await db.SaveChangesAsync(ct);
                roleId = role.Id;

                db.ResourcePermissions.Add(new ResourcePermission
                {
                    RoleId = role.Id,
                    ResourceType = ResourceType.Project,
                    ResourceId = allowed,
                    Permission = Permission.Read
                });

                var user = new User
                {
                    Username = $"scoped-{Guid.NewGuid():N}",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    IsActive = true
                };
                db.Users.Add(user);
                await db.SaveChangesAsync(ct);
                userId = user.Id;
                username = user.Username;

                db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
                await db.SaveChangesAsync(ct);
            }

            using var scoped = fixture.CreateAnonymousClient();
            IntegrationAuth.SetBearer(scoped, await IntegrationAuth.LoginAsync(scoped, username, password, ct));

            using var getAllowed = await scoped.GetAsync($"/api/projects/{allowed}", ct);
            Assert.Equal(HttpStatusCode.OK, getAllowed.StatusCode);

            using var getRefused = await scoped.GetAsync($"/api/projects/{refused}", ct);
            Assert.Equal(HttpStatusCode.Forbidden, getRefused.StatusCode);
        }
        finally
        {
            foreach (var id in projectIds)
            {
                using var delete = await admin.DeleteAsync($"/api/projects/{id}", ct);
                delete.EnsureSuccessStatusCode();
            }

            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (userId is { } uid)
            {
                await db.UserRoles.Where(item => item.UserId == uid).ExecuteDeleteAsync(ct);
                await db.Users.Where(item => item.Id == uid).ExecuteDeleteAsync(ct);
            }

            if (roleId is { } rid)
            {
                await db.ResourcePermissions.Where(item => item.RoleId == rid).ExecuteDeleteAsync(ct);
                await db.Roles.Where(item => item.Id == rid).ExecuteDeleteAsync(ct);
            }
        }
    }

    private static async Task<int> CreateProjectAsync(HttpClient admin, string name, string description, CancellationToken ct)
    {
        using var response = await admin.PostAsJsonAsync(
            "/api/projects", new CreateProjectRequest { Name = name, Description = description }, ct);
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, ct);
        return project!.Id;
    }
}
