// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-2, on the real host and PostgreSQL (the IX_Roles_OrganizationId_Name index): a role name is unique within its
/// organization, not site-wide. Two organizations name a role alike; the same organization twice is refused, and the
/// refusal never names the other organization; a role named in an assignment is the organization's own; and the
/// rights of a holder of A's role are those of A's role only, whatever B's role of the same name grants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleNamePerOrganizationIntegrationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(
        RbacAuditSeed.Tenant A, RbacAuditSeed.Tenant B,
        (int Id, string Username) AdminOfA, (int Id, string Username) AdminOfB, string SharedName);

    private static async Task<Setup> SeedAsync(AetheusWebApplicationFactory factory, string label)
    {
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, $"{label}a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, $"{label}b");
        var adminOfA = await RbacAuditSeed.CreateUserAsync(factory.Services, $"{label}-oa", (RoleNames.OrganizationAdmin, a.OrganizationId));
        var adminOfB = await RbacAuditSeed.CreateUserAsync(factory.Services, $"{label}-ob", (RoleNames.OrganizationAdmin, b.OrganizationId));
        return new Setup(a, b, adminOfA, adminOfB, $"{label}-shared-{Guid.NewGuid():N}"[..30]);
    }

    private static async Task<HttpResponseMessage> CreateRoleAsync(HttpClient client, string name, int organizationId, params string[] keys) =>
        await client.PostAsJsonAsync("/api/roles",
            new CreateRoleRequest { Name = name, OrganizationId = organizationId, PermissionKeys = [.. keys] },
            IntegrationJsonOptions.Default, Ct);

    [Fact]
    public async Task TwoOrganizations_NameARoleAlike_TheSameOrganizationTwiceIsRefused_WithoutNamingTheOther()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "rn1");
        using var adminOfA = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username);
        using var adminOfB = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfB.Username);

        var ofA = await CreateRoleAsync(adminOfA, s.SharedName, s.A.OrganizationId);
        Assert.Equal(HttpStatusCode.Created, ofA.StatusCode);
        // Before R-2 this was "Role 'X' already exists": a refusal that also told B that A has the role.
        var ofB = await CreateRoleAsync(adminOfB, s.SharedName, s.B.OrganizationId);
        Assert.True(ofB.StatusCode == HttpStatusCode.Created, await ofB.Content.ReadAsStringAsync(Ct));
        var roleOfA = (await ofA.Content.ReadFromJsonAsync<RoleDto>(IntegrationJsonOptions.Default, Ct))!;
        var roleOfB = (await ofB.Content.ReadFromJsonAsync<RoleDto>(IntegrationJsonOptions.Default, Ct))!;
        Assert.NotEqual(roleOfA.Id, roleOfB.Id);
        Assert.Equal(s.B.OrganizationId, roleOfB.OrganizationId);

        // The same organization twice: refused, and the answer is about B's own role, nothing of A.
        var twiceInB = await CreateRoleAsync(adminOfB, s.SharedName, s.B.OrganizationId);
        Assert.Equal(HttpStatusCode.Conflict, twiceInB.StatusCode);
        var refusal = await twiceInB.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(s.A.Marker, refusal, StringComparison.Ordinal);
        using (var problem = System.Text.Json.JsonDocument.Parse(refusal))
            Assert.Equal($"Role '{s.SharedName}' already exists.", problem.RootElement.GetProperty("message").GetString());

        // A rename and a clone are checked within the role's organization too.
        var other = await CreateRoleAsync(adminOfB, $"{s.SharedName}-2", s.B.OrganizationId);
        var otherOfB = (await other.Content.ReadFromJsonAsync<RoleDto>(IntegrationJsonOptions.Default, Ct))!;
        Assert.Equal(HttpStatusCode.Conflict, (await adminOfB.PutAsJsonAsync($"/api/roles/{otherOfB.Id}",
            new UpdateRoleRequest { Name = s.SharedName, Description = "" }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        var clone = await adminOfA.PostAsync($"/api/roles/{roleOfA.Id}/clone", null, Ct);
        Assert.Equal(HttpStatusCode.Created, clone.StatusCode);
        Assert.Equal($"{s.SharedName} (Copy)", (await clone.Content.ReadFromJsonAsync<RoleDto>(IntegrationJsonOptions.Default, Ct))!.Name);

        // A site role's name stays everybody's, in any case: no organization makes its own "admin".
        Assert.Equal(HttpStatusCode.Conflict, (await CreateRoleAsync(adminOfA, "admin", s.A.OrganizationId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CreateRoleAsync(adminOfA, RoleNames.Reader, s.A.OrganizationId)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.Roles.CountAsync(role => role.Name == s.SharedName, Ct));
    }

    [Fact]
    public async Task TheIndex_RefusesTheSameNameTwiceInOneOrganization_AndAmongSiteRoles()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "rn2");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Roles.AddRange(
            new Role { Name = s.SharedName, OrganizationId = s.A.OrganizationId },
            new Role { Name = s.SharedName, OrganizationId = s.B.OrganizationId });
        await db.SaveChangesAsync(Ct);

        db.Roles.Add(new Role { Name = s.SharedName, OrganizationId = s.A.OrganizationId });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        db.ChangeTracker.Clear();
        // NULLS NOT DISTINCT: two site roles of one name are refused as well.
        db.Roles.Add(new Role { Name = RoleNames.Admin });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task AHolderOfAsRole_GetsAsRightsOnly_WhateverBsRoleOfTheSameNameGrants()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "rn3");
        using var adminOfA = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username);
        using var adminOfB = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfB.Username);
        // A's role reads servers; B's role of the same name manages servers.
        Assert.Equal(HttpStatusCode.Created, (await CreateRoleAsync(adminOfA, s.SharedName, s.A.OrganizationId, PermissionKeys.ServerView)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateRoleAsync(adminOfB, s.SharedName, s.B.OrganizationId, PermissionKeys.ServerManage)).StatusCode);

        // The Organization Admin of A creates an account in A holding the role by its name: it is A's role.
        var name = $"rn3-u-{Guid.NewGuid():N}"[..24];
        var created = await adminOfA.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = name,
            Email = $"{name}@roles.test",
            OrganizationRoles = [new OrganizationRoleAssignmentDto { OrganizationId = s.A.OrganizationId, Role = s.SharedName }]
        }, IntegrationJsonOptions.Default, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var user = (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, Ct))!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var held = await db.UserOrganizationRoles.AsNoTracking().Where(row => row.UserId == user.Id)
                .Select(row => new { row.OrganizationId, RoleOrganizationId = row.Role.OrganizationId }).SingleAsync(Ct);
            Assert.Equal(s.A.OrganizationId, held.OrganizationId);
            Assert.Equal(s.A.OrganizationId, held.RoleOrganizationId);
        }

        await IntegrationUsers.SetPasswordAsync(factory.Services, user.Id, IntegrationUsers.Password);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);
        var summary = (await client.GetFromJsonAsync<UserPermissionSummaryDto>("/api/users/me/permissions", IntegrationJsonOptions.Default, Ct))!;
        Assert.Contains(summary.PermissionKeys, key => key.Key == PermissionKeys.ServerView && key.OrganizationId == s.A.OrganizationId);
        Assert.DoesNotContain(summary.PermissionKeys, key => key.Key == PermissionKeys.ServerManage);
        Assert.DoesNotContain(summary.PermissionKeys, key => key.OrganizationId == s.B.OrganizationId);
        // And the real authorization agrees: A's server is read, B's is not.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/servers/{s.A.ServerId}", Ct)).StatusCode);
        Assert.Contains((await client.GetAsync($"/api/servers/{s.B.ServerId}", Ct)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        // The Organization Admin of B cannot give A's account B's role by its name for A: there it names A's role,
        // which B does not administer.
        var crossed = await adminOfB.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = $"rn3-x-{Guid.NewGuid():N}"[..24],
            Email = $"rn3x{Guid.NewGuid():N}@roles.test",
            OrganizationRoles = [new OrganizationRoleAssignmentDto { OrganizationId = s.A.OrganizationId, Role = s.SharedName }]
        }, IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, crossed.StatusCode);
    }
}
