// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// BACK-008: the unique index of the registry includes Source, so it never makes a Declared claim and a Manual
/// reservation collide. Real PostgreSQL, two independent contexts per race (two requests, as in production):
/// whatever the interleaving, a port ends up with one claiming owner, never a Declared and a Manual one.
/// Not covered here: the HTTP layer in front of the service (the race is decided below it).
/// </summary>
public sealed class PortRegistryCrossSourceConcurrencyIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private const int Races = 20;

    private static PortRegistryService NewService(AppDbContext db) =>
        new(new PortRegistryRepository(db), TimeProvider.System, NullLogger<PortRegistryService>.Instance);

    [Fact]
    public async Task ADeclarationAfterAManualReservation_IsRefused()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var serverId = (await SeedServersAsync(1, ct))[0];
        await using var db = NewContext();
        var service = NewService(db);
        await service.AddManualReservationAsync(serverId, new CreatePortReservationRequest { Port = 15000, OwnerLabel = "legacy-crm" }, ct);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.DeclareAsync(serverId, [15000], "project:1", "alpha", null, ct));

        Assert.Equal([PortReservationSource.Manual], await ClaimSourcesAsync(serverId, 15000, ct));
    }

    [Fact]
    public async Task ConcurrentManualReservationAndDeclaration_NeverBothHoldThePort()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var serverId = (await SeedServersAsync(1, ct))[0];

        for (var race = 0; race < Races; race++)
        {
            var port = 15100 + race;
            await using var contextA = NewContext();
            await using var contextB = NewContext();
            var manual = SucceedsAsync(() => NewService(contextA).AddManualReservationAsync(
                serverId, new CreatePortReservationRequest { Port = port, OwnerLabel = $"manual-{race}" }, ct));
            var declared = SucceedsAsync(() => NewService(contextB).DeclareAsync(
                serverId, [port], $"project:{race + 1}", $"declared-{race}", null, ct));
            var outcomes = await Task.WhenAll(manual, declared);

            Assert.True(outcomes.Count(won => won) == 1, $"Race {race}: both or neither claim succeeded ({outcomes[0]}, {outcomes[1]}).");
            Assert.Single(await ClaimSourcesAsync(serverId, port, ct));
        }
    }

    [Fact]
    public async Task ConcurrentManualReservationAndAllocation_NeverBothHoldThePort()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var servers = await SeedServersAsync(Races, ct);

        for (var race = 0; race < Races; race++)
        {
            // A one-port window: the allocation can only hand out the port the manual reservation wants.
            var serverId = servers[race];
            await using var contextA = NewContext();
            await using var contextB = NewContext();
            var manual = SucceedsAsync(() => NewService(contextA).AddManualReservationAsync(
                serverId, new CreatePortReservationRequest { Port = WindowPort, OwnerLabel = $"manual-{race}" }, ct));
            var allocated = SucceedsAsync(() => NewService(contextB).AllocateAsync(
                serverId, 1, $"project:{race + 1}", $"allocated-{race}", null, ct));
            var outcomes = await Task.WhenAll(manual, allocated);

            Assert.True(outcomes.Count(won => won) == 1, $"Race {race}: both or neither claim succeeded ({outcomes[0]}, {outcomes[1]}).");
            Assert.Single(await ClaimSourcesAsync(serverId, WindowPort, ct));
        }
    }

    private const int WindowPort = 16000;

    /// <summary>A refused claim is a conflict; anything else is a real defect and is rethrown.</summary>
    private static async Task<bool> SucceedsAsync(Func<Task> claim)
    {
        try
        {
            await Task.Yield();
            await claim();
            return true;
        }
        catch (ConflictException)
        {
            return false;
        }
    }

    private async Task<List<PortReservationSource>> ClaimSourcesAsync(int serverId, int port, CancellationToken ct)
    {
        await using var verify = NewContext();
        return await verify.ServerPortReservations.AsNoTracking()
            .Where(row => row.ServerId == serverId && row.Port == port && row.Source != PortReservationSource.Observed)
            .Select(row => row.Source)
            .ToListAsync(ct);
    }

    private async Task<List<int>> SeedServersAsync(int count, CancellationToken ct)
    {
        await using var seed = NewContext();
        var organization = new Organization { Name = "Cross-source ports", Slug = $"xports-{Guid.NewGuid():N}" };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(ct);
        var servers = Enumerable.Range(0, count).Select(index => new Server
        {
            Name = $"xports-{index}",
            Hostname = $"xports-{index}-host",
            OrganizationId = organization.Id,
            Status = ServerStatus.Online
        }).ToList();
        seed.Servers.AddRange(servers);
        await seed.SaveChangesAsync(ct);
        seed.ServerPortRanges.AddRange(servers.Select(server => new ServerPortRange
        {
            ServerId = server.Id,
            From = WindowPort,
            To = WindowPort,
            UpdatedAt = DateTime.UtcNow
        }));
        await seed.SaveChangesAsync(ct);
        return [.. servers.Select(server => server.Id)];
    }
}
