// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The Administration section for organization administrators (owner decision 2026-10-28), on the real host
/// and PostgreSQL: the Organization Admin of A lists only A's users, organizations, roles and mail templates;
/// everything of B is refused (403 or 404), by id guessing too; it never grants the site Admin role nor makes
/// a role of every organization, nor changes a built-in role; its roles are A's. The site Admin is unchanged.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrganizationAdminSectionIsolationIntegrationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HttpStatusCode[] Refused = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private sealed record Setup(
        RbacAuditSeed.Tenant A, RbacAuditSeed.Tenant B,
        (int Id, string Username) AdminOfA, (int Id, string Username) MemberOfA, (int Id, string Username) MemberOfB,
        int RoleOfB);

    private async Task<Setup> SeedAsync(AetheusWebApplicationFactory factory, string label)
    {
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, $"{label}a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, $"{label}b");
        var adminOfA = await RbacAuditSeed.CreateUserAsync(factory.Services, $"{label}-oadm", (RoleNames.OrganizationAdmin, a.OrganizationId));
        var memberOfA = await RbacAuditSeed.CreateUserAsync(factory.Services, $"{label}-mema", (RoleNames.Reader, a.OrganizationId));
        var memberOfB = await RbacAuditSeed.CreateUserAsync(factory.Services, $"{label}-memb", (RoleNames.Contributor, b.OrganizationId));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roleOfB = new Role { Name = $"{label}-b-role-{Guid.NewGuid():N}"[..40], Description = "B's own role", OrganizationId = b.OrganizationId };
        db.Roles.Add(roleOfB);
        await db.SaveChangesAsync(Ct);
        return new Setup(a, b, adminOfA, memberOfA, memberOfB, roleOfB.Id);
    }

    [Fact]
    public async Task OrganizationAdminOfA_ListsOnlyAsUsersAndOrganizations()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "lst");
        using var client = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username);

        var users = await ReadAsync<PaginatedResult<UserDto>>(client, "/api/users?page=1&pageSize=200");
        Assert.Contains(users.Items, user => user.Id == s.MemberOfA.Id);
        Assert.DoesNotContain(users.Items, user => user.Id == s.MemberOfB.Id);
        // As on the organization page (RBAC audit, finding 7): no member's e-mail, no other organization.
        Assert.All(users.Items, user => Assert.Null(user.Email));
        Assert.All(users.Items, user => Assert.All(user.OrganizationRoles, held => Assert.Equal(s.A.OrganizationId, held.OrganizationId)));

        Assert.Contains((await client.GetAsync($"/api/users?organizationId={s.B.OrganizationId}", Ct)).StatusCode, Refused);
        // The account page stays the site Admin's: a member of A is not opened by its id either.
        Assert.Contains((await client.GetAsync($"/api/users/{s.MemberOfB.Id}", Ct)).StatusCode, Refused);

        var organizations = await ReadAsync<PaginatedResult<OrganizationDto>>(client, "/api/organizations?page=1&pageSize=200");
        Assert.Equal([s.A.OrganizationId], organizations.Items.Select(organization => organization.Id));
        Assert.Contains((await client.GetAsync($"/api/organizations/{s.B.OrganizationId}", Ct)).StatusCode, Refused);
        // The switcher's choice: its own organization, never the other one.
        var chosen = await ReadAsync<PaginatedResult<OrganizationDto>>(client, $"/api/organizations?page=1&pageSize=200&organizationId={s.A.OrganizationId}");
        Assert.Equal([s.A.OrganizationId], chosen.Items.Select(organization => organization.Id));
        Assert.Contains((await client.GetAsync($"/api/organizations?organizationId={s.B.OrganizationId}", Ct)).StatusCode, Refused);

        var administered = await ReadAsync<List<AdministeredOrganizationDto>>(client, "/api/organizations/administered");
        Assert.Equal([s.A.OrganizationId], administered.Select(organization => organization.Id));
    }

    [Fact]
    public async Task OrganizationAdminOfA_ReachesNoRoleOfB_ByListOrById()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "rol");
        using var client = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username);

        var created = await client.PostAsJsonAsync("/api/roles",
            new CreateRoleRequest { Name = $"rol-a-{Guid.NewGuid():N}"[..30], OrganizationId = s.A.OrganizationId, PermissionKeys = [PermissionKeys.ServerView] },
            IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var roleOfA = (await created.Content.ReadFromJsonAsync<RoleDto>(IntegrationJsonOptions.Default, Ct))!;
        Assert.Equal(s.A.OrganizationId, roleOfA.OrganizationId);

        var roles = await ReadAsync<PaginatedResult<RoleDto>>(client, "/api/roles?page=1&pageSize=200");
        Assert.Contains(roles.Items, role => role.Id == roleOfA.Id);
        Assert.DoesNotContain(roles.Items, role => role.Id == s.RoleOfB);
        Assert.All(roles.Items, role => Assert.True(role.OrganizationId == s.A.OrganizationId || RoleNames.BuiltIn.Contains(role.Name),
            $"{role.Name} listed to the Organization Admin of A"));

        // Every route naming B's role, by id: refused, nothing changed.
        var b = s.RoleOfB;
        Assert.Contains((await client.GetAsync($"/api/roles/{b}", Ct)).StatusCode, Refused);
        Assert.Contains((await client.GetAsync($"/api/roles/{b}/permissions", Ct)).StatusCode, Refused);
        Assert.Contains((await client.GetAsync($"/api/roles/{b}/permission-keys", Ct)).StatusCode, Refused);
        Assert.Contains((await client.GetAsync($"/api/roles/{b}/users", Ct)).StatusCode, Refused);
        Assert.Contains((await client.GetAsync($"/api/roles/{b}/available-users", Ct)).StatusCode, Refused);
        Assert.Contains((await client.PutAsJsonAsync($"/api/roles/{b}", new UpdateRoleRequest { Name = "hijacked" }, IntegrationJsonOptions.Default, Ct)).StatusCode, Refused);
        Assert.Contains((await client.PutAsJsonAsync($"/api/roles/{b}/permission-keys",
            new SetRolePermissionKeysRequest { Keys = [PermissionKeys.ServerManage] }, IntegrationJsonOptions.Default, Ct)).StatusCode, Refused);
        Assert.Contains((await client.PutAsJsonAsync($"/api/roles/{b}/permissions",
            new SetResourcePermissionsRequest(), IntegrationJsonOptions.Default, Ct)).StatusCode, Refused);
        Assert.Contains((await client.PostAsync($"/api/roles/{b}/clone", null, Ct)).StatusCode, Refused);
        Assert.Contains((await client.PostAsJsonAsync($"/api/roles/{b}/users",
            new AddUserToRoleRequest { UserId = s.MemberOfB.Id, OrganizationId = s.B.OrganizationId }, IntegrationJsonOptions.Default, Ct)).StatusCode, Refused);
        Assert.Contains((await client.DeleteAsync($"/api/roles/{b}/users/{s.MemberOfB.Id}?organizationId={s.B.OrganizationId}", Ct)).StatusCode, Refused);
        Assert.Contains((await client.DeleteAsync($"/api/roles/{b}", Ct)).StatusCode, Refused);
        // A role in B, made by the Organization Admin of A: refused.
        Assert.Contains((await client.PostAsJsonAsync("/api/roles",
            new CreateRoleRequest { Name = $"rol-x-{Guid.NewGuid():N}"[..30], OrganizationId = s.B.OrganizationId },
            IntegrationJsonOptions.Default, Ct)).StatusCode, Refused);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var untouched = await db.Roles.AsNoTracking().SingleAsync(role => role.Id == b, Ct);
        Assert.NotEqual("hijacked", untouched.Name);
        Assert.Equal(s.B.OrganizationId, untouched.OrganizationId);
        Assert.Equal(0, await db.ResourcePermissions.CountAsync(row => row.RoleId == b, Ct));
    }

    [Fact]
    public async Task OrganizationAdmin_NeverGrantsTheSiteAdminRole_NorMakesAGlobalRole_NorChangesABuiltInRole()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "esc");
        using var client = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username);
        int adminRoleId, readerRoleId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            adminRoleId = await db.Roles.Where(role => role.Name == RoleNames.Admin).Select(role => role.Id).SingleAsync(Ct);
            readerRoleId = await db.Roles.Where(role => role.Name == RoleNames.Reader).Select(role => role.Id).SingleAsync(Ct);
        }

        // The site Admin role, site-wide or "for A": refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/roles/{adminRoleId}/users",
            new AddUserToRoleRequest { UserId = s.MemberOfA.Id }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/roles/{adminRoleId}/users",
            new AddUserToRoleRequest { UserId = s.MemberOfA.Id, OrganizationId = s.A.OrganizationId }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/roles/{adminRoleId}/users",
            new AddUserToRoleRequest { UserId = s.AdminOfA.Id }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        // A role of every organization: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/roles",
            new CreateRoleRequest { Name = $"esc-g-{Guid.NewGuid():N}"[..30] }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        // The built-in roles' grants are the platform's: read, never changed.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/roles/{readerRoleId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{readerRoleId}/permission-keys",
            new SetRolePermissionKeysRequest { Keys = [PermissionKeys.ServerManage] }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{readerRoleId}/permissions",
            new SetResourcePermissionsRequest(), IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{adminRoleId}",
            new UpdateRoleRequest { Name = RoleNames.Admin, Description = "mine" }, IntegrationJsonOptions.Default, Ct)).StatusCode);

        // Its own role: given to a member of A for A, never to a user of B (not a member of A), never for B.
        var created = await client.PostAsJsonAsync("/api/roles",
            new CreateRoleRequest { Name = $"esc-a-{Guid.NewGuid():N}"[..30], OrganizationId = s.A.OrganizationId }, IntegrationJsonOptions.Default, Ct);
        var roleOfA = (await created.Content.ReadFromJsonAsync<RoleDto>(IntegrationJsonOptions.Default, Ct))!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/roles/{roleOfA.Id}/users",
            new AddUserToRoleRequest { UserId = s.MemberOfA.Id, OrganizationId = s.A.OrganizationId }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Contains((await client.PostAsJsonAsync($"/api/roles/{roleOfA.Id}/users",
            new AddUserToRoleRequest { UserId = s.MemberOfB.Id, OrganizationId = s.A.OrganizationId }, IntegrationJsonOptions.Default, Ct)).StatusCode, Refused);
        Assert.Contains((await client.PostAsJsonAsync($"/api/roles/{roleOfA.Id}/users",
            new AddUserToRoleRequest { UserId = s.MemberOfB.Id, OrganizationId = s.B.OrganizationId }, IntegrationJsonOptions.Default, Ct)).StatusCode, Refused);
        // A grant on one of B's servers would name B's resource in A's role: refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/roles/{roleOfA.Id}/permissions",
            new SetResourcePermissionsRequest { Permissions = [new ResourcePermissionEntry { ResourceType = ResourceType.Server, ResourceId = s.B.ServerId, Permission = Permission.Read }] },
            IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/roles/{roleOfA.Id}/permissions",
            new SetResourcePermissionsRequest { Permissions = [new ResourcePermissionEntry { ResourceType = ResourceType.Server, ResourceId = s.A.ServerId, Permission = Permission.Read }] },
            IntegrationJsonOptions.Default, Ct)).StatusCode);

        // Not even the site Admin gives an organization's role for another organization.
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/roles/{roleOfA.Id}/users",
            new AddUserToRoleRequest { UserId = s.MemberOfB.Id, OrganizationId = s.B.OrganizationId }, IntegrationJsonOptions.Default, Ct)).StatusCode);

        using var check = factory.Services.CreateScope();
        var rows = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await rows.UserRoles.AnyAsync(held => held.RoleId == adminRoleId
            && (held.UserId == s.MemberOfA.Id || held.UserId == s.AdminOfA.Id), Ct));
        Assert.False(await rows.UserOrganizationRoles.AnyAsync(held => held.RoleId == roleOfA.Id && held.UserId == s.MemberOfB.Id, Ct));
    }

    [Fact]
    public async Task MailTemplates_TheOrganizationAdminEditsOnlyItsOrganizationsVersion_AndItIsWhatItsMailsUse()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "mtp");
        using var client = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username);
        var kind = MailTemplateKind.NotificationEvent;
        var mine = new SaveMailTemplateRequest { Subject = $"A {Guid.NewGuid():N} {{{{Event}}}}", Body = "<p>{{Summary}}</p>" };

        var list = await ReadAsync<List<MailTemplateSummaryDto>>(client, $"/api/mail-templates?organizationId={s.A.OrganizationId}");
        Assert.All(list, line => Assert.True(MailTemplateScopes.IsOrganizationKind(line.Kind)));
        Assert.All(list, line => Assert.Equal(s.A.OrganizationId, line.OrganizationId));
        // The platform's templates, and B's: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/mail-templates", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/mail-templates/{kind}/en", mine, IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/mail-templates?organizationId={s.B.OrganizationId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/mail-templates/{kind}/en?organizationId={s.B.OrganizationId}", mine, IntegrationJsonOptions.Default, Ct)).StatusCode);
        // A kind no organization owns stays the platform's.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(
            $"/api/mail-templates/{MailTemplateKind.DailyRecap}/en?organizationId={s.A.OrganizationId}", mine, IntegrationJsonOptions.Default, Ct)).StatusCode);

        var saved = await client.PutAsJsonAsync($"/api/mail-templates/{kind}/en?organizationId={s.A.OrganizationId}", mine, IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var read = await ReadAsync<MailTemplateDto>(client, $"/api/mail-templates/{kind}/en?organizationId={s.A.OrganizationId}");
        Assert.True(read.IsCustomized);
        Assert.Equal(mine.Subject, read.Subject);

        // The platform's version is not changed; B's mails and the platform's keep it.
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        Assert.NotEqual(mine.Subject, (await ReadAsync<MailTemplateDto>(admin, $"/api/mail-templates/{kind}/en")).Subject);
        using (var scope = factory.Services.CreateScope())
        {
            var renderer = scope.ServiceProvider.GetRequiredService<IMailTemplateRenderer>();
            var data = MailTemplateCatalog.Samples(kind, MailTemplateLanguages.English);
            var style = MailRecipientStyle.Default with { Language = MailTemplateLanguages.English };
            var forA = await renderer.RenderAsync(kind, data, style, s.A.OrganizationId, Ct);
            var forB = await renderer.RenderAsync(kind, data, style, s.B.OrganizationId, Ct);
            Assert.StartsWith("A ", forA.Subject, StringComparison.Ordinal);
            Assert.DoesNotContain(mine.Subject[..10], forB.Subject, StringComparison.Ordinal);
        }

        // Restoring: the organization reads the platform's version again.
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/mail-templates/{kind}/en?organizationId={s.A.OrganizationId}", Ct)).StatusCode);
        Assert.False((await ReadAsync<MailTemplateDto>(client, $"/api/mail-templates/{kind}/en?organizationId={s.A.OrganizationId}")).IsCustomized);
    }

    [Fact]
    public async Task TheAdministeredOrganizations_CarryTheKeysHeldInEach_AndTheSiteAdminHoldsThemAll()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "key");
        // Members in A; only the roles key in B: the Users page must not offer B, the Roles page both.
        var user = await IntegrationUsers.CreateAsync(factory.Services, "key-mix", [], s.A.OrganizationId,
            [PermissionKeys.OrganizationMembersManage]);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rolesOnly = new Role { Name = $"key-b-{Guid.NewGuid():N}"[..30], Description = "roles key in B", OrganizationId = s.B.OrganizationId };
            db.Roles.Add(rolesOnly);
            await db.SaveChangesAsync(Ct);
            db.RolePermissionGrants.Add(new RolePermissionGrant { RoleId = rolesOnly.Id, PermissionKey = PermissionKeys.OrganizationRolesManage });
            db.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = user.Id, OrganizationId = s.B.OrganizationId, RoleId = rolesOnly.Id });
            db.OrganizationMembers.Add(new OrganizationMember { UserId = user.Id, OrganizationId = s.B.OrganizationId, Role = OrganizationRole.Member });
            await db.SaveChangesAsync(Ct);
        }

        using (var client = await IntegrationUsers.LoginClientAsync(factory, user.Username))
        {
            var administered = await ReadAsync<List<AdministeredOrganizationDto>>(client, "/api/organizations/administered");
            Assert.Equal([PermissionKeys.OrganizationMembersManage], administered.Single(o => o.Id == s.A.OrganizationId).Keys);
            Assert.Equal([PermissionKeys.OrganizationRolesManage], administered.Single(o => o.Id == s.B.OrganizationId).Keys);
        }

        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var all = await ReadAsync<List<AdministeredOrganizationDto>>(admin, "/api/organizations/administered");
        Assert.All(all, organization => Assert.Equal(PermissionCatalog.OrganizationAdministrationKeys, organization.Keys));
    }

    [Fact]
    public async Task AMemberWithoutTheKeys_GetsNoneOfTheSection_AndTheSiteAdminKeepsEverything()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "noa");
        var contributor = await RbacAuditSeed.CreateUserAsync(factory.Services, "noa-con", (RoleNames.Contributor, s.A.OrganizationId));
        using (var client = await IntegrationUsers.LoginClientAsync(factory, contributor.Username))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/roles", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/organizations", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/mail-templates?organizationId={s.A.OrganizationId}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/roles",
                new CreateRoleRequest { Name = $"noa-{Guid.NewGuid():N}"[..30], OrganizationId = s.A.OrganizationId }, IntegrationJsonOptions.Default, Ct)).StatusCode);
            Assert.Empty(await ReadAsync<List<AdministeredOrganizationDto>>(client, "/api/organizations/administered"));
        }

        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var users = await ReadAsync<PaginatedResult<UserDto>>(admin, "/api/users?page=1&pageSize=200&sortBy=Id&sortDescending=true");
        Assert.Contains(users.Items, user => user.Id == s.MemberOfA.Id);
        Assert.Contains(users.Items, user => user.Id == s.MemberOfB.Id);
        Assert.Contains(users.Items, user => user.Id == s.MemberOfB.Id && user.Email is not null);
        var roles = await ReadAsync<PaginatedResult<RoleDto>>(admin, "/api/roles?page=1&pageSize=200&sortBy=createdAt&sortDescending=true");
        Assert.Contains(roles.Items, role => role.Id == s.RoleOfB && role.OrganizationId == s.B.OrganizationId);
        // The site Admin makes a role of one organization, or of every organization.
        var scoped = await admin.PostAsJsonAsync("/api/roles",
            new CreateRoleRequest { Name = $"noa-b-{Guid.NewGuid():N}"[..30], OrganizationId = s.B.OrganizationId }, IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.Created, scoped.StatusCode);
        Assert.Equal(s.B.OrganizationId, (await scoped.Content.ReadFromJsonAsync<RoleDto>(IntegrationJsonOptions.Default, Ct))!.OrganizationId);
        var global = await admin.PostAsJsonAsync("/api/roles",
            new CreateRoleRequest { Name = $"noa-g-{Guid.NewGuid():N}"[..30] }, IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.Created, global.StatusCode);
        Assert.Null((await global.Content.ReadFromJsonAsync<RoleDto>(IntegrationJsonOptions.Default, Ct))!.OrganizationId);
        var organizations = await ReadAsync<List<AdministeredOrganizationDto>>(admin, "/api/organizations/administered");
        Assert.Contains(organizations, organization => organization.Id == s.A.OrganizationId);
        Assert.Contains(organizations, organization => organization.Id == s.B.OrganizationId);
        // The organization filter of the site Admin.
        var usersOfB = await ReadAsync<PaginatedResult<UserDto>>(admin, $"/api/users?page=1&pageSize=200&organizationId={s.B.OrganizationId}");
        Assert.Equal([s.MemberOfB.Id], usersOfB.Items.Select(user => user.Id));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/mail-templates?organizationId={s.B.OrganizationId}", Ct)).StatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(IntegrationJsonOptions.Default, Ct))!;
    }
}
