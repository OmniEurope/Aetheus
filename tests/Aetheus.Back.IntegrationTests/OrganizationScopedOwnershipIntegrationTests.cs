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
public sealed class OrganizationScopedOwnershipIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string AdminPassword = "Integr@tion-Test-Admin-Pwd-2026";

    private static Task<string> LoginAsync(HttpClient client, string email, string password)
        => IntegrationAuth.LoginByEmailAsync(client, email, password, TestContext.Current.CancellationToken);

    private static void SetBearer(HttpClient client, string token)
        => IntegrationAuth.SetBearer(client, token);

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
