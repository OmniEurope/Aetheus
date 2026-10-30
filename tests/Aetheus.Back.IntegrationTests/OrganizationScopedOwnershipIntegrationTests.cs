// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Organizations;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Verifies org-scoped ownership isolation: servers and projects created under one
/// organization are not visible to users who belong to a different organization.
/// <para>
/// The <c>ResourceAuthorizationService</c> unions org-membership resource ids into
/// <c>accessibleIds</c> for Server and Project. These tests exercise the full pipeline
/// (controller → authz → repository → PostgreSQL) to prove cross-org isolation works
/// end-to-end, which the InMemory suite cannot do because it neither enforces FK
/// ownership constraints nor runs the real org-membership join queries.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrganizationScopedOwnershipIntegrationTests(PostgresFixture fixture)
{
    private const string AdminPassword = "Integr@tion-Test-Admin-Pwd-2026";

    private static Task<string> LoginAsync(HttpClient client, string email, string password)
        => IntegrationAuth.LoginByEmailAsync(client, email, password, TestContext.Current.CancellationToken);

    private static void SetBearer(HttpClient client, string token)
        => IntegrationAuth.SetBearer(client, token);

    [Fact]
    public async Task ProjectCreatedInOrgA_NotVisibleToUserInOrgB()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var adminToken = await LoginAsync(client, IntegrationAuth.AdminEmail, AdminPassword);
        SetBearer(client, adminToken);

        // Create two organizations
        var orgAResp = await client.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest
        {
            Name = $"Org A {Guid.NewGuid():N}",
            Slug = $"org-a-{Guid.NewGuid():N}",
            Description = "First org"
        }, cancellationToken: TestContext.Current.CancellationToken);
        orgAResp.EnsureSuccessStatusCode();
        var orgA = await orgAResp.Content.ReadFromJsonAsync<OrganizationDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        var orgBResp = await client.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest
        {
            Name = $"Org B {Guid.NewGuid():N}",
            Slug = $"org-b-{Guid.NewGuid():N}",
            Description = "Second org"
        }, cancellationToken: TestContext.Current.CancellationToken);
        orgBResp.EnsureSuccessStatusCode();
        var orgB = await orgBResp.Content.ReadFromJsonAsync<OrganizationDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Create a project in Org A
        var projResp = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"OrgA-Project-{Guid.NewGuid():N}",
            Description = "Belongs to Org A",
            OrganizationId = orgA!.Id
        }, cancellationToken: TestContext.Current.CancellationToken);
        projResp.EnsureSuccessStatusCode();
        var project = await projResp.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        // ADR-059 (R-46): user B holds the built-in Contributor role for Org B only. Its wildcard
        // Write permission is bounded by that organization, so it must never reach Org A.
        string userBName;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var orgOnlyRole = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .SingleAsync(db.Roles.Where(role => role.Name == "Contributor"), TestContext.Current.CancellationToken);

            userBName = $"orgb-user-{Guid.NewGuid():N}";
            var userB = new User
            {
                Username = userBName,
                Email = $"{userBName}@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("OrgB-User-Pwd-2026!"),
                IsActive = true
            };
            db.Users.Add(userB);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = userB.Id, OrganizationId = orgB!.Id, RoleId = orgOnlyRole.Id });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            // Add user B to Org B
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = orgB!.Id,
                UserId = userB.Id,
                Role = OrganizationRole.Member
            });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        // Login as user B
        client.DefaultRequestHeaders.Authorization = null;
        var userBToken = await LoginAsync(client, $"{userBName}@example.com", "OrgB-User-Pwd-2026!");
        SetBearer(client, userBToken);

        // User B should NOT be able to read the Org A project
        var getProj = await client.GetAsync($"/api/projects/{project!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, getProj.StatusCode);
    }

    [Fact]
    public async Task UserInOrg_CanSeeProjectsOwnedByTheirOrg()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var adminToken = await LoginAsync(client, IntegrationAuth.AdminEmail, AdminPassword);
        SetBearer(client, adminToken);

        // Create an organization
        var orgResp = await client.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest
        {
            Name = $"My Org {Guid.NewGuid():N}",
            Slug = $"my-org-{Guid.NewGuid():N}",
            Description = "Test org"
        }, cancellationToken: TestContext.Current.CancellationToken);
        orgResp.EnsureSuccessStatusCode();
        var org = await orgResp.Content.ReadFromJsonAsync<OrganizationDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Create a project in this org
        var projResp = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"MyOrg-Project-{Guid.NewGuid():N}",
            Description = "Belongs to my org",
            OrganizationId = org!.Id
        }, cancellationToken: TestContext.Current.CancellationToken);
        projResp.EnsureSuccessStatusCode();
        var project = await projResp.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        // A project of another organization: the member must not reach it.
        var otherOrgResp = await client.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest
        {
            Name = $"Other Org {Guid.NewGuid():N}",
            Slug = $"other-org-{Guid.NewGuid():N}",
            Description = "Not the member's org"
        }, cancellationToken: TestContext.Current.CancellationToken);
        otherOrgResp.EnsureSuccessStatusCode();
        var otherOrg = await otherOrgResp.Content.ReadFromJsonAsync<OrganizationDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        var otherProjResp = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"OtherOrg-Project-{Guid.NewGuid():N}",
            Description = "Belongs to another org",
            OrganizationId = otherOrg!.Id
        }, cancellationToken: TestContext.Current.CancellationToken);
        otherProjResp.EnsureSuccessStatusCode();
        var otherProject = await otherProjResp.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        // ADR-059 (R-45): membership alone grants nothing; a member holds the Reader role for its
        // organization. That role reads the organization's projects and nothing of another one.
        string memberName;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var readerRole = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .SingleAsync(db.Roles.Where(role => role.Name == "Reader"), TestContext.Current.CancellationToken);

            memberName = $"member-{Guid.NewGuid():N}";
            var member = new User
            {
                Username = memberName,
                Email = $"{memberName}@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Member-Test-Pwd-2026!"),
                IsActive = true
            };
            db.Users.Add(member);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = member.Id, OrganizationId = org!.Id, RoleId = readerRole.Id });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = org!.Id,
                UserId = member.Id,
                Role = OrganizationRole.Member
            });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        // Login as the member
        client.DefaultRequestHeaders.Authorization = null;
        var memberToken = await LoginAsync(client, $"{memberName}@example.com", "Member-Test-Pwd-2026!");
        SetBearer(client, memberToken);

        // Member CAN read the project owned by their org, through the Reader role held for it
        var getProj = await client.GetAsync($"/api/projects/{project!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getProj.StatusCode);

        // and only that one
        var getOther = await client.GetAsync($"/api/projects/{otherProject!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, getOther.StatusCode);
    }

    [Fact]
    public async Task OrganizationMembership_DoesNotAuthorizePoolOrEnvironmentMutationOfServer()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        SetBearer(client, await LoginAsync(client, IntegrationAuth.AdminEmail, AdminPassword));
        var orgResponse = await client.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest
        {
            Name = $"Read-only server org {Guid.NewGuid():N}",
            Slug = $"read-only-server-{Guid.NewGuid():N}",
            Description = "Organization membership grants server read only"
        }, cancellationToken: TestContext.Current.CancellationToken);
        orgResponse.EnsureSuccessStatusCode();
        var organization = await orgResponse.Content.ReadFromJsonAsync<OrganizationDto>(
            IntegrationJsonOptions.Default,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(organization);

        const string password = "Org-ReadOnly-Server-Pwd-2026!";
        string username;
        int serverId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var role = new Role
            {
                Name = $"PoolEnvironmentWriter-{Guid.NewGuid():N}",
                Description = "Can create pools and environments, but cannot write servers"
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            db.ResourcePermissions.AddRange(
                new ResourcePermission
                {
                    RoleId = role.Id,
                    ResourceType = ResourceType.AgentPool,
                    ResourceId = null,
                    Permission = Permission.Write
                },
                new ResourcePermission
                {
                    RoleId = role.Id,
                    ResourceType = ResourceType.Environment,
                    ResourceId = null,
                    Permission = Permission.Write
                });

            username = $"org-server-reader-{Guid.NewGuid():N}";
            var user = new User
            {
                Username = username,
                Email = $"{username}@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                IsActive = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            db.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = user.Id, OrganizationId = organization!.Id, RoleId = role.Id });
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = organization!.Id,
                UserId = user.Id,
                Role = OrganizationRole.Member
            });
            var server = new Server
            {
                Name = $"Read-only server {Guid.NewGuid():N}",
                Hostname = $"readonly-{Guid.NewGuid():N}.test",
                IpAddress = "192.0.2.44",
                OrganizationId = organization.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Servers.Add(server);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            serverId = server.Id;
        }

        client.DefaultRequestHeaders.Authorization = null;
        SetBearer(client, await LoginAsync(client, $"{username}@example.com", password));

        var poolResponse = await client.PostAsJsonAsync("/api/agent-pools", new CreateAgentPoolRequest
        {
            Name = $"Forbidden pool {Guid.NewGuid():N}",
            ServerIds = [serverId]
        }, cancellationToken: TestContext.Current.CancellationToken);
        var environmentResponse = await client.PostAsJsonAsync("/api/environments", new CreateEnvironmentRequest
        {
            Name = $"Forbidden environment {Guid.NewGuid():N}",
            ServerIds = [serverId]
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, poolResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, environmentResponse.StatusCode);
    }
}

// Helpers used by the EF queries above - xUnit v3 + EF require explicit async.
file static class QueryExtensions
{
    internal static async Task<T> FirstAsync<T>(this IQueryable<T> q, AppDbContext _)
        => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(q);
}
