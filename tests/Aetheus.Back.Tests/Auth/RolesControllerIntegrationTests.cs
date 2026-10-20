// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.Tests;

public class RolesControllerIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _anonClient;

    public RolesControllerIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomWebApplicationFactory.GenerateTestToken());
        _anonClient = _factory.CreateClient();
    }

    // ──── Auth ────

    [Fact]
    public async Task GetRoles_Anonymous_Returns401()
    {
        var response = await _anonClient.GetAsync("/api/roles", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ──── CRUD ────

    [Fact]
    public async Task CreateRole_ValidRequest_Returns201()
    {
        var response = await _client.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = $"TestRole_{Guid.NewGuid():N}",
            Description = "Integration test role"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var role = await response.Content.ReadFromJsonAsync<RoleDto>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(role);
        Assert.True(role.Id > 0);
    }

    [Fact]
    public async Task CreateRole_DuplicateName_Returns409()
    {
        var name = $"DupRole_{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/roles", new CreateRoleRequest { Name = name }, cancellationToken: TestContext.Current.CancellationToken);

        var response = await _client.PostAsJsonAsync("/api/roles", new CreateRoleRequest { Name = name }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetRole_Exists_ReturnsRole()
    {
        var created = await CreateTestRoleAsync();

        var response = await _client.GetAsync($"/api/roles/{created.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var role = await response.Content.ReadFromJsonAsync<RoleDto>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(role);
        Assert.Equal(created.Name, role.Name);
    }

    [Fact]
    public async Task GetRole_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/roles/99999", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRole_ValidRequest_ReturnsUpdated()
    {
        var created = await CreateTestRoleAsync();

        var response = await _client.PutAsJsonAsync($"/api/roles/{created.Id}", new UpdateRoleRequest
        {
            Name = created.Name + "_updated",
            Description = "Updated description"
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<RoleDto>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Contains("_updated", updated.Name);
    }

    [Fact]
    public async Task DeleteRole_Exists_Returns204()
    {
        var created = await CreateTestRoleAsync();

        var response = await _client.DeleteAsync($"/api/roles/{created.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await _client.GetAsync($"/api/roles/{created.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteRole_NotFound_Returns404()
    {
        var response = await _client.DeleteAsync("/api/roles/99999", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ──── Permissions ────

    [Fact]
    public async Task SetPermissions_ValidMatrix_Returns204()
    {
        var role = await CreateTestRoleAsync();

        var permissions = new SetResourcePermissionsRequest
        {
            Permissions =
            [
                new ResourcePermissionEntry { ResourceType = ResourceType.Server, Permission = Permission.Read },
                new ResourcePermissionEntry { ResourceType = ResourceType.Pipeline, Permission = Permission.Write },
                new ResourcePermissionEntry { ResourceType = ResourceType.Vault, Permission = Permission.Admin }
            ]
        };

        var response = await _client.PutAsJsonAsync($"/api/roles/{role.Id}/permissions", permissions, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetPermissions_AfterSet_ReturnsCorrectEntries()
    {
        var role = await CreateTestRoleAsync();

        var permissions = new SetResourcePermissionsRequest
        {
            Permissions =
            [
                new ResourcePermissionEntry { ResourceType = ResourceType.Server, Permission = Permission.Read },
                new ResourcePermissionEntry { ResourceType = ResourceType.Pipeline, Permission = Permission.Write }
            ]
        };

        await _client.PutAsJsonAsync($"/api/roles/{role.Id}/permissions", permissions, cancellationToken: TestContext.Current.CancellationToken);

        var response = await _client.GetAsync($"/api/roles/{role.Id}/permissions", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<List<ResourcePermissionDto>>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task SetPermissions_ReplacesPrevious()
    {
        var role = await CreateTestRoleAsync();

        await _client.PutAsJsonAsync($"/api/roles/{role.Id}/permissions", new SetResourcePermissionsRequest
        {
            Permissions = [new ResourcePermissionEntry { ResourceType = ResourceType.Server, Permission = Permission.Read }]
        }, cancellationToken: TestContext.Current.CancellationToken);

        await _client.PutAsJsonAsync($"/api/roles/{role.Id}/permissions", new SetResourcePermissionsRequest
        {
            Permissions = [new ResourcePermissionEntry { ResourceType = ResourceType.Vault, Permission = Permission.Admin }]
        }, cancellationToken: TestContext.Current.CancellationToken);

        var response = await _client.GetAsync($"/api/roles/{role.Id}/permissions", cancellationToken: TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<List<ResourcePermissionDto>>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(ResourceType.Vault, result[0].ResourceType);
    }

    // ──── User Permissions ────

    [Fact]
    public async Task GetMyPermissions_Authenticated_ReturnsPermissions()
    {
        var response = await _client.GetAsync("/api/users/me/permissions", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<UserPermissionSummaryDto>(
            TestJsonOptions.Default,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Contains("Admin", summary.Roles);
        // Build the bootstrap identity summary from token role claims rather than a same-name user
        // row.
        Assert.Equal(0, summary.UserId);
    }

    [Fact]
    public async Task GetMyPermissions_Anonymous_Returns401()
    {
        var response = await _anonClient.GetAsync("/api/users/me/permissions", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetEffectivePermissions_AdminUser_ReturnsOk()
    {
        int userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FindAsync([1], TestContext.Current.CancellationToken);
            userId = user?.Id ?? 1;
        }

        var response = await _client.GetAsync($"/api/users/{userId}/effective-permissions", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<UserPermissionSummaryDto>(TestJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(userId, summary.UserId);
    }

    // ──── Protected Roles ────

    [Fact]
    public async Task DeleteAdminRole_Returns400()
    {
        int adminRoleId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var adminRole = db.Roles.FirstOrDefault(r => r.Name == "Admin");
            if (adminRole is null)
            {
                adminRole = new Role { Name = "Admin", Description = "Admin" };
                db.Roles.Add(adminRole);
                await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            }
            adminRoleId = adminRole.Id;
        }

        var response = await _client.DeleteAsync($"/api/roles/{adminRoleId}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ──── Helpers ────

    private async Task<RoleDto> CreateTestRoleAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = $"Role_{Guid.NewGuid():N}",
            Description = "Test role"
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RoleDto>(TestJsonOptions.Default))!;
    }
}
