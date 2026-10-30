// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;
using Aetheus.Shared.Components.Auth;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-18 on the real host and PostgreSQL: a role carries its creation date and the date of its last
/// change, the change being a rename or a new set of permissions; the seeded roles took the migration's
/// run time instead of the year 1.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleDatesIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ARole_IsDatedAtCreation_AndItsRenameAndItsPermissionsMoveUpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var before = DateTime.UtcNow.AddMinutes(-1);
        var created = await (await admin.PostAsJsonAsync("/api/roles",
            new CreateRoleRequest { Name = $"dated-{Guid.NewGuid():N}"[..30], Description = "d" }, ct))
            .Content.ReadFromJsonAsync<RoleDto>(IntegrationJsonOptions.Default, ct);
        var first = await ReadAsync(admin, created!.Id, ct);
        Assert.True(first.CreatedAt >= before);
        Assert.Equal(first.CreatedAt, first.UpdatedAt);

        await Task.Delay(20, ct);
        (await admin.PutAsJsonAsync($"/api/roles/{first.Id}/permissions", new SetResourcePermissionsRequest
        {
            Permissions = [new ResourcePermissionEntry { ResourceType = ResourceType.Project, Permission = Permission.Read }]
        }, ct)).EnsureSuccessStatusCode();
        var afterPermissions = await ReadAsync(admin, first.Id, ct);
        Assert.True(afterPermissions.UpdatedAt > first.UpdatedAt);
        Assert.Equal(first.CreatedAt, afterPermissions.CreatedAt);

        await Task.Delay(20, ct);
        (await admin.PutAsJsonAsync($"/api/roles/{first.Id}",
            new UpdateRoleRequest { Name = first.Name + "-b", Description = "e" }, ct)).EnsureSuccessStatusCode();
        var afterRename = await ReadAsync(admin, first.Id, ct);
        Assert.True(afterRename.UpdatedAt > afterPermissions.UpdatedAt);
    }

    [Fact]
    public async Task TheSeededRoles_AreDatedWithTheClockOfTheDatabase_NotTheYearOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var page = await admin.GetFromJsonAsync<PaginatedResult<RoleDto>>("/api/roles?page=1&pageSize=100", IntegrationJsonOptions.Default, ct);

        Assert.Contains(page!.Items, role => role.Name == RoleNames.Admin);
        Assert.All(page.Items, role => Assert.True(role.CreatedAt.Year >= 2026, $"{role.Name}: {role.CreatedAt:o}"));
    }

    private static async Task<RoleDto> ReadAsync(HttpClient client, int id, CancellationToken ct) =>
        (await client.GetFromJsonAsync<RoleDto>($"/api/roles/{id}", IntegrationJsonOptions.Default, ct))!;
}
