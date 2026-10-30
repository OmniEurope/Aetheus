// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-10 (d), on the real host and PostgreSQL: the holder of org.members.manage creates a user in the
/// organizations it administers only, with the built-in roles but Admin and those organizations' own roles;
/// another organization, a site-wide role, the Admin role and another organization's role are refused and
/// create nothing. An e-mail already used says no more than the existing "add member" flow. The site Admin
/// is unchanged.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrganizationAdminUserCreationIntegrationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(
        RbacAuditSeed.Tenant A, RbacAuditSeed.Tenant B, (int Id, string Username) AdminOfA,
        (int Id, string Username) MemberOfB, string RoleOfA, string RoleOfB, string GlobalRole);

    private static async Task<Setup> SeedAsync(AetheusWebApplicationFactory factory, string label)
    {
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, $"{label}a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, $"{label}b");
        var adminOfA = await RbacAuditSeed.CreateUserAsync(factory.Services, $"{label}-oadm", (RoleNames.OrganizationAdmin, a.OrganizationId));
        var memberOfB = await RbacAuditSeed.CreateUserAsync(factory.Services, $"{label}-memb", (RoleNames.Contributor, b.OrganizationId));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roleOfA = new Role { Name = $"{label}-a-role-{Guid.NewGuid():N}"[..40], Description = "A's own role", OrganizationId = a.OrganizationId };
        var roleOfB = new Role { Name = $"{label}-b-role-{Guid.NewGuid():N}"[..40], Description = "B's own role", OrganizationId = b.OrganizationId };
        // A role of every organization the site Admin made: held per organization, yet not one A administers.
        var global = new Role { Name = $"{label}-g-role-{Guid.NewGuid():N}"[..40], Description = "every organization's role" };
        db.Roles.AddRange(roleOfA, roleOfB, global);
        await db.SaveChangesAsync(Ct);
        return new Setup(a, b, adminOfA, memberOfB, roleOfA.Name, roleOfB.Name, global.Name);
    }

    private static CreateUserRequest NewUser(string label, List<string>? roles = null, params (int OrganizationId, string Role)[] held)
    {
        var name = $"{label}-{Guid.NewGuid():N}"[..30];
        return new CreateUserRequest
        {
            Username = name,
            Email = $"{name}@create.test",
            Roles = roles ?? [],
            OrganizationRoles = [.. held.Select(item => new OrganizationRoleAssignmentDto { OrganizationId = item.OrganizationId, Role = item.Role })]
        };
    }

    [Fact]
    public async Task OrganizationAdminOfA_CreatesAUserInA_WithABuiltInRoleOrAsOwnRole_AndNowhereElse()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "cre");
        using var client = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username);

        var reader = NewUser("cre-r", null, (s.A.OrganizationId, RoleNames.Reader));
        var created = await client.PostAsJsonAsync("/api/users", reader, IntegrationJsonOptions.Default, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode} {await created.Content.ReadAsStringAsync(Ct)}");
        var dto = (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, Ct))!;
        Assert.Empty(dto.Roles);
        Assert.All(dto.OrganizationRoles, held => Assert.Equal(s.A.OrganizationId, held.OrganizationId));

        var own = NewUser("cre-o", null, (s.A.OrganizationId, s.RoleOfA));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/users", own, IntegrationJsonOptions.Default, Ct)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(row => row.Username == reader.Username, Ct);
        Assert.Equal([s.A.OrganizationId], await db.OrganizationMembers.Where(row => row.UserId == user.Id).Select(row => row.OrganizationId).ToListAsync(Ct));
        Assert.Equal([(s.A.OrganizationId, RoleNames.Reader)], (await db.UserOrganizationRoles.Where(row => row.UserId == user.Id)
            .Select(row => new { row.OrganizationId, row.Role.Name }).ToListAsync(Ct)).Select(row => (row.OrganizationId, row.Name)));
        Assert.False(await db.UserRoles.AnyAsync(row => row.UserId == user.Id, Ct));
        var ownUser = await db.Users.AsNoTracking().SingleAsync(row => row.Username == own.Username, Ct);
        Assert.True(await db.UserOrganizationRoles.AnyAsync(row => row.UserId == ownUser.Id && row.OrganizationId == s.A.OrganizationId && row.Role.Name == s.RoleOfA, Ct));
    }

    [Fact]
    public async Task OrganizationAdminOfA_IsRefused_AnotherOrganization_SiteWideRoles_TheAdminRole_AndBsOwnRole()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "ref");
        using var client = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username);
        var refused = new List<(CreateUserRequest Request, HttpStatusCode Expected)>
        {
            (NewUser("ref-b", null, (s.B.OrganizationId, RoleNames.Reader)), HttpStatusCode.Forbidden),
            (NewUser("ref-ab", null, (s.A.OrganizationId, RoleNames.Reader), (s.B.OrganizationId, RoleNames.Reader)), HttpStatusCode.Forbidden),
            (NewUser("ref-adm", [RoleNames.Admin], (s.A.OrganizationId, RoleNames.Reader)), HttpStatusCode.Forbidden),
            (NewUser("ref-site", [RoleNames.Reader], (s.A.OrganizationId, RoleNames.Reader)), HttpStatusCode.Forbidden),
            (NewUser("ref-adma", null, (s.A.OrganizationId, RoleNames.Admin)), HttpStatusCode.Forbidden),
            (NewUser("ref-rob", null, (s.A.OrganizationId, s.RoleOfB)), HttpStatusCode.Forbidden),
            (NewUser("ref-glob", null, (s.A.OrganizationId, s.GlobalRole)), HttpStatusCode.Forbidden),
            // Without an organization the account would be nobody's: the Organization Admin names one.
            (NewUser("ref-none"), HttpStatusCode.BadRequest),
        };

        foreach (var (request, expected) in refused)
        {
            var response = await client.PostAsJsonAsync("/api/users", request, IntegrationJsonOptions.Default, Ct);
            Assert.True(response.StatusCode == expected, $"{request.Username}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = refused.Select(item => item.Request.Username).ToList();
        Assert.False(await db.Users.AnyAsync(row => names.Contains(row.Username), Ct));
    }

    [Fact]
    public async Task AnEmailAlreadyUsed_SaysOnlyThatItIsTaken_AndAMemberWithoutTheKeyCreatesNothing()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "eml");
        string takenEmail;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            takenEmail = (await db.Users.AsNoTracking().SingleAsync(row => row.Id == s.MemberOfB.Id, Ct)).Email!;
        }
        using (var client = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username))
        {
            var request = NewUser("eml-dup", null, (s.A.OrganizationId, RoleNames.Reader)) with { Email = takenEmail.ToUpperInvariant() };
            var response = await client.PostAsJsonAsync("/api/users", request, IntegrationJsonOptions.Default, Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ApiError>(IntegrationJsonOptions.Default, Ct))!;
            Assert.Equal(UserErrorCodes.EmailInUse, error.Code);
            // Nothing about the account holding it: neither its name, nor its id, nor its organization. Read from
            // the parsed fields that say something (never the correlation id, a random token that may hold any
            // digit), and ids only as whole numbers, so "3" is not found inside "43".
            var disclosed = string.Join(
                "\n",
                [
                    error.Message, error.Detail ?? string.Empty,
                    .. (error.Errors ?? []).SelectMany(item => item.Value.Prepend(item.Key)),
                    .. (error.Problems ?? []).SelectMany(problem => problem.Parameters.SelectMany(item => new[] { item.Key, item.Value }).Prepend(problem.Code))
                ]);
            Assert.DoesNotContain(s.MemberOfB.Username, disclosed, StringComparison.OrdinalIgnoreCase);
            foreach (var id in new[] { s.MemberOfB.Id, s.B.OrganizationId })
                Assert.DoesNotMatch($@"(?<!\d){id}(?!\d)", disclosed);
        }

        var contributor = await RbacAuditSeed.CreateUserAsync(factory.Services, "eml-con", (RoleNames.Contributor, s.A.OrganizationId));
        using var member = await IntegrationUsers.LoginClientAsync(factory, contributor.Username);
        var denied = NewUser("eml-con", null, (s.A.OrganizationId, RoleNames.Reader));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/users", denied, IntegrationJsonOptions.Default, Ct)).StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().Users.AnyAsync(row => row.Username == denied.Username, Ct));
    }

    [Fact]
    public async Task TheRolesOfferedAtCreation_AreTheBuiltInOnesButAdmin_AndTheOrganizationsOwn_ForItsOrganizationsOnly()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "asg");
        using var client = await IntegrationUsers.LoginClientAsync(factory, s.AdminOfA.Username);

        var response = await client.GetAsync($"/api/roles/assignable?organizationId={s.A.OrganizationId}&page=1&pageSize=200", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        var names = (await response.Content.ReadFromJsonAsync<PaginatedResult<RoleDto>>(IntegrationJsonOptions.Default, Ct))!
            .Items.Select(role => role.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(RoleNames.BuiltInForOneOrganization.Append(s.RoleOfA).Order(StringComparer.Ordinal), names);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/roles/assignable?organizationId={s.B.OrganizationId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task TheSiteAdmin_StillCreatesInAnyOrganization_AndGivesTheAdminRole()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var s = await SeedAsync(factory, "sad");
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var inB = NewUser("sad-b", null, (s.B.OrganizationId, s.RoleOfB));
        var created = await admin.PostAsJsonAsync("/api/users", inB, IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(inB.Email, (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, Ct))!.Email);
        var siteAdmin = NewUser("sad-adm", [RoleNames.Admin]);
        var granted = await admin.PostAsJsonAsync("/api/users", siteAdmin, IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        Assert.Equal([RoleNames.Admin], (await granted.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, Ct))!.Roles);
        // The site Admin too is refused a role of B held for A.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users",
            NewUser("sad-x", null, (s.A.OrganizationId, s.RoleOfB)), IntegrationJsonOptions.Default, Ct)).StatusCode);
    }
}
