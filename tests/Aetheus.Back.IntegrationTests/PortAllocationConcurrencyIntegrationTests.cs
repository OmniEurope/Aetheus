// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Concurrent PostgreSQL port allocation must respect real unique constraints, which EF
/// InMemory cannot verify.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PortAllocationConcurrencyIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private PortRegistryService NewService(AppDbContext db) =>
        new(new PortRegistryRepository(db), TimeProvider.System, NullLogger<PortRegistryService>.Instance);

    [Fact]
    public async Task ConcurrentAllocations_NeverHandTheSamePortTwice()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;

        int serverId;
        await using (var seed = NewContext())
        {
            var organization = new Organization { Name = "Ports", Slug = $"ports-{Guid.NewGuid():N}" };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(ct);

            var server = new Server
            {
                Name = "port-alloc",
                Hostname = "port-alloc-host",
                OrganizationId = organization.Id,
                Status = ServerStatus.Online
            };
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(ct);
            serverId = server.Id;

            // A window barely wider than what the two callers ask for, so any overlap in their choices
            // really collides instead of being hidden by a large search space.
            seed.ServerPortRanges.Add(new ServerPortRange
            {
                ServerId = serverId,
                From = 10000,
                To = 10005,
                UpdatedAt = DateTime.UtcNow
            });
            await seed.SaveChangesAsync(ct);
        }

        // Two independent contexts: two requests, as in production, not two calls on one tracked graph.
        await using var contextA = NewContext();
        await using var contextB = NewContext();
        var serviceA = NewService(contextA);
        var serviceB = NewService(contextB);

        var taskA = AllocateOrNullAsync(serviceA, serverId, "project:1", "alpha", ct);
        var taskB = AllocateOrNullAsync(serviceB, serverId, "project:2", "beta", ct);
        var results = await Task.WhenAll(taskA, taskB);

        var granted = results.Where(ports => ports is not null).SelectMany(ports => ports!).ToList();

        // Whatever the interleaving, the union carries no duplicate: either both succeeded on disjoint
        // ports, or the loser was refused by the unique index rather than being handed a taken port.
        Assert.Equal(granted.Count, granted.Distinct().Count());
        Assert.NotEmpty(granted);

        await using var verify = NewContext();
        var rows = await verify.ServerPortReservations
            .AsNoTracking()
            .Where(reservation => reservation.ServerId == serverId)
            .ToListAsync(ct);
        Assert.Equal(rows.Count, rows.Select(row => row.Port).Distinct().Count());
    }

    /// <summary>
    /// A losing allocation surfaces as a conflict, which is the unique index doing its job rather than an
    /// unexpected failure. Anything else is rethrown so a real defect is not swallowed.
    /// </summary>
    private static async Task<List<int>?> AllocateOrNullAsync(
        PortRegistryService service, int serverId, string ownerKey, string label, CancellationToken ct)
    {
        try
        {
            return await service.AllocateAsync(serverId, 3, ownerKey, label, null, ct);
        }
        catch (Aetheus.Back.Exceptions.ConflictException)
        {
            return null;
        }
    }
}
