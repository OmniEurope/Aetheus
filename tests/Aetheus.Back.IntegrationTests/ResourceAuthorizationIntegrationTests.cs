// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// End-to-end exercise of resource-level permissions through the real HTTP pipeline.
/// <para>
/// Creates users with different role/permission configurations and verifies that the
/// <see cref="Services.ResourceAuthorizationService"/> + controller-level permission
/// checks correctly gate access to owned vs. unowned resources. These flows are only
/// partially testable in the InMemory unit suite because the permission checks depend
/// on real relational joins (role → resource_permission → resource → org membership).
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ResourceAuthorizationIntegrationTests(PostgresFixture fixture)
{
    private static Task<string> LoginAsAdminAsync(HttpClient client)
        => IntegrationAuth.LoginAsAdminAsync(client, TestContext.Current.CancellationToken);

    private static void SetBearer(HttpClient client, string token)
        => IntegrationAuth.SetBearer(client, token);

    [Fact]
    public async Task ReaderUser_CanReadProjects_ButCannotCreateProject()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        // 1. Login as admin and create a Reader user
        var adminToken = await LoginAsAdminAsync(client);
        SetBearer(client, adminToken);

        var createUserResp = await client.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = $"reader-{Guid.NewGuid():N}",
            Password = "Reader-Test-Pwd-2026!",
            Roles = ["Reader"],
            // D1: a flagged account only reaches the password change; this one tests RBAC.
            MustChangePassword = false
        }, cancellationToken: TestContext.Current.CancellationToken);
        createUserResp.EnsureSuccessStatusCode();

        var createdUser = await createUserResp.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(createdUser);

        // 2. Login as the new Reader user
        client.DefaultRequestHeaders.Authorization = null;
        var readerLoginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = createdUser!.Username,
            Password = "Reader-Test-Pwd-2026!"
        }, cancellationToken: TestContext.Current.CancellationToken);
        readerLoginResp.EnsureSuccessStatusCode();
        var readerLogin = await readerLoginResp.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        SetBearer(client, readerLogin!.Token);

        // 3. Reader CAN list projects (even if empty - 200 is the point)
        var listResp = await client.GetAsync("/api/projects", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);

        // 4. Reader CANNOT create a project (no Write permission on Project)
        var createProjResp = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = "Forbidden Project",
            Description = "Should be blocked"
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, createProjResp.StatusCode);
    }

    [Fact]
    public async Task UserWithResourceScopedPermission_CanOnlyAccessGrantedResource()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var adminToken = await LoginAsAdminAsync(client);
        SetBearer(client, adminToken);

        // Create two projects as admin
        var proj1Resp = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"Allowed-{Guid.NewGuid():N}",
            Description = "User can see this"
        }, cancellationToken: TestContext.Current.CancellationToken);
        proj1Resp.EnsureSuccessStatusCode();
        var proj1 = await proj1Resp.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        var proj2Resp = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"Forbidden-{Guid.NewGuid():N}",
            Description = "User cannot see this"
        }, cancellationToken: TestContext.Current.CancellationToken);
        proj2Resp.EnsureSuccessStatusCode();
        var proj2 = await proj2Resp.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Create a custom role with Read permission only on proj1
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var role = new Role { Name = $"ScopedReader-{Guid.NewGuid():N}", Description = "read one project" };
            db.Roles.Add(role);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.ResourcePermissions.Add(new ResourcePermission
            {
                RoleId = role.Id,
                ResourceType = ResourceType.Project,
                ResourceId = proj1!.Id,
                Permission = Permission.Read
            });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var user = new User
            {
                Username = $"scoped-{Guid.NewGuid():N}",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Scoped-Test-Pwd-2026!"),
                IsActive = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            // Login as the scoped user
            client.DefaultRequestHeaders.Authorization = null;
            var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                Username = user.Username,
                Password = "Scoped-Test-Pwd-2026!"
            }, cancellationToken: TestContext.Current.CancellationToken);
            loginResp.EnsureSuccessStatusCode();
            var loginResult = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            SetBearer(client, loginResult!.Token);
        }

        // Scoped user CAN read proj1
        var getProj1 = await client.GetAsync($"/api/projects/{proj1!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getProj1.StatusCode);

        // Scoped user CANNOT read proj2
        var getProj2 = await client.GetAsync($"/api/projects/{proj2!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, getProj2.StatusCode);
    }
}
