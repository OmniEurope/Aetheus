// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-12: an agent pool belongs to one organization. A user whose role is held for organization B (wildcard
/// Admin on pools and Write on servers, bounded by B under ADR-059) lists, reads, changes and deletes B's pools,
/// and never sees nor touches A's: list, get, update and delete of A's pool are refused, and A's pool is intact
/// afterwards. The site Admin sees both.
/// </summary>
public sealed class AgentPoolTenantIsolationHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly HttpStatusCode[] Refused = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    [Fact]
    public async Task UserOfOrganizationB_ReachesOnlyTheAgentPoolsOfB()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userB = await IntegrationUsers.CreateAsync(factory.Services, "pool-tenant-b",
            [
                new IntegrationUsers.Grant(ResourceType.AgentPool, null, Permission.Admin),
                new IntegrationUsers.Grant(ResourceType.Server, null, Permission.Write)
            ],
            b.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);

        // List: B's pool, never A's.
        var list = await client.GetAsync("/api/agent-pools?pageSize=200", ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = await IntegrationUsers.ReadListIdsAsync(list);
        Assert.Contains(b.PoolId, listed);
        Assert.DoesNotContain(a.PoolId, listed);
        Assert.DoesNotContain(a.Marker, await list.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // Get: B's pool yes (positive control), A's refused without its name.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/agent-pools/{b.PoolId}", ct)).StatusCode);
        var foreignGet = await client.GetAsync($"/api/agent-pools/{a.PoolId}", ct);
        Assert.Contains(foreignGet.StatusCode, Refused);
        Assert.DoesNotContain(a.Marker, await foreignGet.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // Update: A's pool refused, even when the body names only B's server; B's pool accepted.
        var foreignUpdate = await client.PutAsJsonAsync($"/api/agent-pools/{a.PoolId}", new UpdateAgentPoolRequest
        {
            Name = $"{b.Marker}-hijack",
            ServerIds = [b.ServerId]
        }, ct);
        Assert.Contains(foreignUpdate.StatusCode, Refused);
        var ownUpdate = await client.PutAsJsonAsync($"/api/agent-pools/{b.PoolId}", new UpdateAgentPoolRequest
        {
            Name = $"{b.Marker}-renamed",
            ServerIds = [b.ServerId]
        }, ct);
        Assert.Equal(HttpStatusCode.OK, ownUpdate.StatusCode);

        // B's pool cannot take A's server.
        var crossServer = await client.PutAsJsonAsync($"/api/agent-pools/{b.PoolId}", new UpdateAgentPoolRequest
        {
            Name = $"{b.Marker}-renamed",
            ServerIds = [a.ServerId]
        }, ct);
        Assert.Contains(crossServer.StatusCode, Refused);

        // Delete: A's pool refused.
        var foreignDelete = await client.DeleteAsync($"/api/agent-pools/{a.PoolId}", ct);
        Assert.Contains(foreignDelete.StatusCode, Refused);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var poolA = await db.AgentPools.AsNoTracking().Include(pool => pool.Servers)
                .SingleAsync(pool => pool.Id == a.PoolId, ct);
            Assert.Equal($"{a.Marker}-pool", poolA.Name);
            Assert.Equal(a.OrganizationId, poolA.OrganizationId);
            Assert.Equal([a.ServerId], poolA.Servers.Select(link => link.ServerId).ToList());
            var poolB = await db.AgentPools.AsNoTracking().Include(pool => pool.Servers)
                .SingleAsync(pool => pool.Id == b.PoolId, ct);
            Assert.Equal([b.ServerId], poolB.Servers.Select(link => link.ServerId).ToList());
        }

        // Own delete works (positive control of the delete path).
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/agent-pools/{b.PoolId}", ct)).StatusCode);
    }

    [Fact]
    public async Task UserOfOrganizationB_CreatesPoolsInB_Only()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userB = await IntegrationUsers.CreateAsync(factory.Services, "pool-creator-b",
            [
                new IntegrationUsers.Grant(ResourceType.AgentPool, null, Permission.Write),
                new IntegrationUsers.Grant(ResourceType.Server, null, Permission.Write)
            ],
            b.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);

        var created = await client.PostAsJsonAsync("/api/agent-pools", new CreateAgentPoolRequest
        {
            Name = $"{b.Marker}-created",
            ServerIds = [b.ServerId]
        }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pool = await created.Content.ReadFromJsonAsync<AgentPoolDto>(IntegrationJsonOptions.Default, ct);
        Assert.NotNull(pool);
        Assert.Equal(b.OrganizationId, pool.OrganizationId);

        var inA = await client.PostAsJsonAsync("/api/agent-pools", new CreateAgentPoolRequest
        {
            Name = $"{b.Marker}-in-a",
            OrganizationId = a.OrganizationId
        }, ct);
        Assert.Contains(inA.StatusCode, Refused);

        var withAServer = await client.PostAsJsonAsync("/api/agent-pools", new CreateAgentPoolRequest
        {
            Name = $"{b.Marker}-with-a-server",
            ServerIds = [a.ServerId]
        }, ct);
        Assert.Contains(withAServer.StatusCode, Refused);
    }

    [Fact]
    public async Task SiteAdmin_SeesThePoolsOfEveryOrganization_AndCannotMixThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var listed = await IntegrationUsers.ReadListIdsAsync(await admin.GetAsync("/api/agent-pools?pageSize=200", ct));
        Assert.Contains(a.PoolId, listed);
        Assert.Contains(b.PoolId, listed);

        // Even the site Admin cannot put a server of A into B's pool: a pool never spans two organizations.
        var mixed = await admin.PutAsJsonAsync($"/api/agent-pools/{b.PoolId}", new UpdateAgentPoolRequest
        {
            Name = $"{b.Marker}-pool",
            ServerIds = [b.ServerId, a.ServerId]
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
    }

    private sealed record Tenant(int OrganizationId, int ServerId, int PoolId, string Marker);

    /// <summary>One organization with a server and a pool holding it.</summary>
    private static async Task<Tenant> SeedTenantAsync(AetheusWebApplicationFactory factory, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var marker = $"pool-tenant-{label}-{Guid.NewGuid():N}";

        var organization = new Organization { Name = marker, Slug = $"p{label}{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var server = new Server
        {
            Name = $"{marker}-server",
            Hostname = $"{marker}.test",
            OrganizationId = organization.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        var pool = new AgentPool
        {
            Name = $"{marker}-pool",
            Description = marker,
            OrganizationId = organization.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Servers = [new AgentPoolServer { ServerId = server.Id }]
        };
        db.AgentPools.Add(pool);
        await db.SaveChangesAsync(ct);
        return new Tenant(organization.Id, server.Id, pool.Id, marker);
    }
}
