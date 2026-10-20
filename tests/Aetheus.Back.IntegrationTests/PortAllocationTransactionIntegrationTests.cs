// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Exercises allocation with the real <see cref="VariableLibraryService"/> and PostgreSQL
/// transactions, including nested scope participation and complete rollback.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PortAllocationTransactionIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(int LibraryId, int ServerId, int ProjectId);

    private async Task<Fixture> SeedAsync()
    {
        await ResetAndMigrateAsync();

        await using var seed = NewContext();
        var organization = new Organization { Name = "Ports tx", Slug = $"ports-tx-{Guid.NewGuid():N}" };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(Ct);

        var server = new Server
        {
            Name = "port-tx",
            Hostname = "port-tx-host",
            OrganizationId = organization.Id,
            Status = ServerStatus.Online
        };
        seed.Servers.Add(server);
        var project = new Project { Name = "Portfolio", OrganizationId = organization.Id };
        seed.Projects.Add(project);
        await seed.SaveChangesAsync(Ct);

        seed.ProjectServers.Add(new ProjectServer
        {
            ProjectId = project.Id,
            ServerId = server.Id,
            Type = ProjectServerType.ExternalHost,
            DisplayName = "prod",
            Host = "prod.local"
        });
        var library = new VariableLibrary { Name = "portfolio-host", ProjectId = project.Id };
        seed.VariableLibraries.Add(library);
        seed.ServerPortRanges.Add(new ServerPortRange
        {
            ServerId = server.Id,
            From = 10000,
            To = 10010,
            UpdatedAt = DateTime.UtcNow
        });
        await seed.SaveChangesAsync(Ct);

        return new Fixture(library.Id, server.Id, project.Id);
    }

    /// <summary>Builds the service graph the way DI does: one context, one scope, real collaborators.</summary>
    private static PortAllocationService BuildService(
        AppDbContext db, IDbTransactionScope scope, IVariableLibraryService libraries) =>
        new(new PortAllocationRepository(db),
            new PortRegistryService(new PortRegistryRepository(db), TimeProvider.System, NullLogger<PortRegistryService>.Instance),
            libraries,
            scope,
            NullLogger<PortAllocationService>.Instance);

    private static VariableLibraryService RealLibraries(AppDbContext db, IDbTransactionScope scope) =>
        new(new VariableLibraryRepository(db),
            scope,
            Substitute.For<IAuditService>(),
            Substitute.For<IEntityChangeNotifier>(),
            TimeProvider.System,
            new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task Allocate_ThroughTheRealLibraryService_WritesBothSides()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();
        var scope = new DbTransactionScope(db);
        var sut = BuildService(db, scope, RealLibraries(db, scope));

        var created = await sut.AllocateIntoLibraryAsync(
            seeded.LibraryId, seeded.ServerId, ["PORT_FRONT", "PORT_BACK"], Ct);

        Assert.Equal(["PORT_FRONT", "PORT_BACK"], created.Select(entry => entry.Key));

        await using var verify = NewContext();
        var entries = await verify.VariableLibraryEntries.AsNoTracking()
            .Where(entry => entry.VariableLibraryId == seeded.LibraryId)
            .OrderBy(entry => entry.Key)
            .ToListAsync(Ct);
        var reservations = await verify.ServerPortReservations.AsNoTracking()
            .Where(reservation => reservation.ServerId == seeded.ServerId)
            .OrderBy(reservation => reservation.Port)
            .ToListAsync(Ct);

        Assert.Equal(2, entries.Count);
        Assert.Equal(2, reservations.Count);
        // The variables really name the ports that were reserved, not two independent numbers.
        Assert.Equal(
            reservations.Select(reservation => reservation.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(),
            entries.Select(entry => entry.Value).Order());
        Assert.All(reservations, reservation =>
            Assert.Equal($"project:{seeded.ProjectId}", reservation.OwnerKey));
    }

    [Fact]
    public async Task Allocate_WhenAnEntryWriteFails_RollsBackTheReservationsToo()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();
        var scope = new DbTransactionScope(db);

        var real = RealLibraries(db, scope);
        var flaky = Substitute.For<IVariableLibraryService>();
        var calls = 0;
        flaky.CreateEntryAsync(Arg.Any<int>(), Arg.Any<CreateVariableEntryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ++calls == 2
                ? throw new InvalidOperationException("entry rejected")
                : real.CreateEntryAsync(
                    call.Arg<int>(), call.Arg<CreateVariableEntryRequest>(), call.Arg<CancellationToken>()));

        var sut = BuildService(db, scope, flaky);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.AllocateIntoLibraryAsync(seeded.LibraryId, seeded.ServerId, ["PORT_FRONT", "PORT_BACK"], Ct));

        await using var verify = NewContext();
        // Neither half survives: not the entry the first call wrote, not the ports both were given.
        Assert.Empty(await verify.VariableLibraryEntries.AsNoTracking()
            .Where(entry => entry.VariableLibraryId == seeded.LibraryId).ToListAsync(Ct));
        Assert.Empty(await verify.ServerPortReservations.AsNoTracking()
            .Where(reservation => reservation.ServerId == seeded.ServerId).ToListAsync(Ct));
    }

    [Fact]
    public async Task NestedScopes_JoinInsteadOfMakingEfRefuseASecondBegin()
    {
        // Nested scope entry must join the existing transaction.
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var scope = new DbTransactionScope(db);

        await scope.BeginTransactionAsync(Ct);
        await scope.BeginTransactionAsync(Ct);
        await scope.CommitAsync(Ct);
        // Still inside the outer level: the inner commit must not have closed it.
        Assert.NotNull(db.Database.CurrentTransaction);
        await scope.CommitAsync(Ct);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task InnerRollback_DoomsTheWholeNesting()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var scope = new DbTransactionScope(db);

        await scope.BeginTransactionAsync(Ct);
        await scope.BeginTransactionAsync(Ct);
        await scope.RollbackAsync(Ct);

        // Committing after an inner rollback would persist half the work, so it refuses out loud.
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync(Ct));
        Assert.Null(db.Database.CurrentTransaction);
    }
}
