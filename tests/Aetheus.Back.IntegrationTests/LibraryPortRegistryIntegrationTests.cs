// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.VariableLibraries.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>The graph DI builds for library writes: the real library service, the real registry and the real
/// handler, over one context and PostgreSQL transactions.</summary>
internal static class LibraryPortWiring
{
    public static PortRegistryService Registry(AppDbContext db) =>
        new(new PortRegistryRepository(db), TimeProvider.System, NullLogger<PortRegistryService>.Instance);

    public static PipelinePortRegistryGuard Guard(AppDbContext db, IPipelineDispatchServerResolver dispatch) =>
        new(dispatch, Registry(db), new LibraryHeldPortSource(new PortAllocationRepository(db)), NullLogger<PipelinePortRegistryGuard>.Instance);

    public static LibraryPortRegistrar Registrar(AppDbContext db) =>
        new(new PortAllocationRepository(db), Registry(db), NullLogger<LibraryPortRegistrar>.Instance);

    public static VariableLibraryService Libraries(AppDbContext db, IDbTransactionScope scope)
    {
        var services = new ServiceCollection()
            .AddSingleton<IDomainEventHandler<VariableLibraryPortsChangedEvent>>(
                new LibraryPortRegistryHandler(Registrar(db)))
            .BuildServiceProvider();
        return new VariableLibraryService(
            new VariableLibraryRepository(db),
            scope,
            Substitute.For<IAuditService>(),
            Substitute.For<IEntityChangeNotifier>(),
            TimeProvider.System,
            new MemoryCache(new MemoryCacheOptions()),
            new DomainEventDispatcher(services, new BackgroundTaskQueue(), NullLogger<DomainEventDispatcher>.Instance));
    }
}

/// <summary>R-30: every <c>PORT_*</c> entry written in a project's library is a declared reservation of that
/// project, on create, update, delete, import, library deletion and move, and for what already exists
/// (backfill). A port another owner holds refuses the write. Real PostgreSQL, real transactions.</summary>
public sealed class LibraryPortRegistryIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(
        int ServerId, int OtherServerId, int ProjectId, int OtherProjectId, int LibraryId, int EnvironmentLibraryId,
        int ProjectLevelLibraryId);

    private async Task<Seeded> SeedAsync()
    {
        await ResetAndMigrateAsync();
        await using var seed = NewContext();
        var organization = new Organization { Name = "Ports", Slug = $"ports-lib-{Guid.NewGuid():N}" };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(Ct);

        var server = new Server { Name = "vps", Hostname = "vps-host", OrganizationId = organization.Id, Status = ServerStatus.Online };
        var other = new Server { Name = "vps-2", Hostname = "vps-2-host", OrganizationId = organization.Id, Status = ServerStatus.Online };
        seed.Servers.AddRange(server, other);
        var aetheus = new Project { Name = "Aetheus", OrganizationId = organization.Id };
        var w10 = new Project { Name = "w10", OrganizationId = organization.Id };
        seed.Projects.AddRange(aetheus, w10);
        await seed.SaveChangesAsync(Ct);

        var host = new ProjectServer
        {
            ProjectId = aetheus.Id,
            ServerId = server.Id,
            Type = ProjectServerType.ExternalHost,
            DisplayName = "prod",
            Host = "prod.local"
        };
        seed.ProjectServers.Add(host);
        var qa = new Environment { Name = "qa", ProjectId = aetheus.Id };
        seed.Environments.Add(qa);
        await seed.SaveChangesAsync(Ct);
        seed.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = qa.Id, ServerId = server.Id });

        var hostLibrary = new VariableLibrary { Name = "aetheus.prod-host", ProjectServerId = host.Id };
        var environmentLibrary = new VariableLibrary { Name = "aetheus.qa", EnvironmentId = qa.Id };
        var projectLibrary = new VariableLibrary { Name = "aetheus.shared", ProjectId = aetheus.Id };
        seed.VariableLibraries.AddRange(hostLibrary, environmentLibrary, projectLibrary);
        seed.ServerPortRanges.Add(new ServerPortRange { ServerId = server.Id, From = 10000, To = 10010, UpdatedAt = DateTime.UtcNow });
        await seed.SaveChangesAsync(Ct);
        return new Seeded(server.Id, other.Id, aetheus.Id, w10.Id, hostLibrary.Id, environmentLibrary.Id, projectLibrary.Id);
    }

    private async Task<List<ServerPortReservation>> ReservationsAsync(int serverId)
    {
        await using var verify = NewContext();
        return await verify.ServerPortReservations.AsNoTracking()
            .Where(reservation => reservation.ServerId == serverId && reservation.Source != PortReservationSource.Observed)
            .OrderBy(reservation => reservation.Port)
            .ToListAsync(Ct);
    }

    private static async Task<VariableEntryDto> AddAsync(Seeded seeded, int libraryId, AppDbContext db, string key, string value)
    {
        var scope = new DbTransactionScope(db);
        return await LibraryPortWiring.Libraries(db, scope)
            .CreateEntryAsync(libraryId, new CreateVariableEntryRequest { Key = key, Value = value }, Ct);
    }

    [Fact]
    public async Task CreatingAPortEntry_DeclaresItForTheLibrarysProject_OnTheLibrarysServer()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();

        await AddAsync(seeded, seeded.LibraryId, db, "PORT_FRONT_GREEN", "10002");

        var held = Assert.Single(await ReservationsAsync(seeded.ServerId));
        Assert.Equal(10002, held.Port);
        Assert.Equal($"project:{seeded.ProjectId}", held.OwnerKey);
        Assert.Equal(PortReservationSource.Declared, held.Source);
        Assert.Equal(seeded.ProjectId, held.ProjectId);
        Assert.Empty(await ReservationsAsync(seeded.OtherServerId));
    }

    [Fact]
    public async Task AnEnvironmentLibrary_DeclaresOnTheEnvironmentsServers()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();

        await AddAsync(seeded, seeded.EnvironmentLibraryId, db, "PORT_FRONT_BLUE", "10004");

        var held = Assert.Single(await ReservationsAsync(seeded.ServerId));
        Assert.Equal((10004, $"project:{seeded.ProjectId}"), (held.Port, held.OwnerKey));
    }

    [Fact]
    public async Task EntriesThatAreNotPorts_DeclareNothing()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();

        await AddAsync(seeded, seeded.LibraryId, db, "DB_HOST", "10002");
        await AddAsync(seeded, seeded.LibraryId, db, "PORT_EMPTY", "");
        await AddAsync(seeded, seeded.LibraryId, db, "PORT_TEXT", "not-a-port");

        Assert.Empty(await ReservationsAsync(seeded.ServerId));
    }

    [Fact]
    public async Task ChangingThePortValue_MovesTheReservation()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();
        var entry = await AddAsync(seeded, seeded.LibraryId, db, "PORT_FRONT_GREEN", "10002");

        await using var update = NewContext();
        var libraries = LibraryPortWiring.Libraries(update, new DbTransactionScope(update));
        await libraries.UpdateEntryAsync(
            seeded.LibraryId, entry.Id, new UpdateVariableEntryRequest { Key = "PORT_FRONT_GREEN", Value = "10003" }, Ct);

        var held = Assert.Single(await ReservationsAsync(seeded.ServerId));
        Assert.Equal(10003, held.Port);
    }

    [Fact]
    public async Task DeletingThePortEntry_ReleasesTheReservation()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();
        var entry = await AddAsync(seeded, seeded.LibraryId, db, "PORT_FRONT_GREEN", "10002");

        await using var delete = NewContext();
        var deleted = await LibraryPortWiring.Libraries(delete, new DbTransactionScope(delete))
            .DeleteEntryAsync(seeded.LibraryId, entry.Id, Ct);

        Assert.True(deleted);
        Assert.Empty(await ReservationsAsync(seeded.ServerId));
    }

    [Fact]
    public async Task DeletingOneOfTwoEntriesHoldingTheSamePort_KeepsTheReservation()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();
        var first = await AddAsync(seeded, seeded.LibraryId, db, "PORT_FRONT_GREEN", "10002");
        await AddAsync(seeded, seeded.EnvironmentLibraryId, db, "PORT_FRONT_BLUE", "10002");

        await using var delete = NewContext();
        await LibraryPortWiring.Libraries(delete, new DbTransactionScope(delete))
            .DeleteEntryAsync(seeded.LibraryId, first.Id, Ct);

        Assert.Equal(10002, Assert.Single(await ReservationsAsync(seeded.ServerId)).Port);
    }

    [Fact]
    public async Task APortAnotherProjectHolds_RefusesTheWrite_NamingTheHolder_AndSavesNothing()
    {
        var seeded = await SeedAsync();
        await using (var holder = NewContext())
        {
            await LibraryPortWiring.Registry(holder).ClaimPortAsync(
                seeded.ServerId, 10000, $"project:{seeded.OtherProjectId}", "w10", seeded.OtherProjectId, Ct);
        }

        await using var db = NewContext();
        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            AddAsync(seeded, seeded.EnvironmentLibraryId, db, "PORT_FRONT_BLUE", "10000"));

        Assert.Contains("PORT_FRONT_BLUE", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'w10'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'vps'", refusal.Message, StringComparison.Ordinal);
        await using var verify = NewContext();
        Assert.Empty(await verify.VariableLibraryEntries.AsNoTracking()
            .Where(entry => entry.VariableLibraryId == seeded.EnvironmentLibraryId).ToListAsync(Ct));
        var held = Assert.Single(await ReservationsAsync(seeded.ServerId));
        Assert.Equal($"project:{seeded.OtherProjectId}", held.OwnerKey);
    }

    [Fact]
    public async Task AManualReservationOfAnotherOwner_RefusesAnUpdate_AndKeepsTheOldValue()
    {
        var seeded = await SeedAsync();
        await using (var manual = NewContext())
        {
            await LibraryPortWiring.Registry(manual).AddManualReservationAsync(
                seeded.ServerId, new CreatePortReservationRequest { Port = 10005, OwnerLabel = "legacy-app" }, Ct);
        }

        await using var db = NewContext();
        var entry = await AddAsync(seeded, seeded.LibraryId, db, "PORT_BACK_GREEN", "10002");

        await using var update = NewContext();
        await Assert.ThrowsAsync<ConflictException>(() =>
            LibraryPortWiring.Libraries(update, new DbTransactionScope(update)).UpdateEntryAsync(
                seeded.LibraryId, entry.Id, new UpdateVariableEntryRequest { Key = "PORT_BACK_GREEN", Value = "10005" }, Ct));

        await using var verify = NewContext();
        Assert.Equal("10002", (await verify.VariableLibraryEntries.AsNoTracking().SingleAsync(row => row.Id == entry.Id, Ct)).Value);
        Assert.Equal([10002, 10005], (await ReservationsAsync(seeded.ServerId)).Select(row => row.Port));
    }

    [Fact]
    public async Task APortOnlySeenListening_IsRegistered_NotRefused()
    {
        var seeded = await SeedAsync();
        await using (var scan = NewContext())
        {
            await LibraryPortWiring.Registry(scan).ReplaceObservedAsync(
                seeded.ServerId, [new ObservedPortDto { Port = 10002, Holder = "docker-proxy", Interface = "0.0.0.0" }], DateTime.UtcNow, Ct);
        }

        await using var db = NewContext();
        await AddAsync(seeded, seeded.LibraryId, db, "PORT_FRONT_GREEN", "10002");

        Assert.Equal(10002, Assert.Single(await ReservationsAsync(seeded.ServerId)).Port);
    }

    [Fact]
    public async Task ImportingPortEntries_DeclaresThem()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();

        await LibraryPortWiring.Libraries(db, new DbTransactionScope(db)).ImportEntriesAsync(
            seeded.LibraryId,
            [
                new CreateVariableEntryRequest { Key = "PORT_FRONT_GREEN", Value = "10002" },
                new CreateVariableEntryRequest { Key = "NAME", Value = "x" }
            ], Ct);

        Assert.Equal(10002, Assert.Single(await ReservationsAsync(seeded.ServerId)).Port);
    }

    [Fact]
    public async Task DeletingTheLibrary_ReleasesItsPorts()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();
        await AddAsync(seeded, seeded.LibraryId, db, "PORT_FRONT_GREEN", "10002");

        await using var delete = NewContext();
        Assert.True(await LibraryPortWiring.Libraries(delete, new DbTransactionScope(delete))
            .DeleteLibraryAsync(seeded.LibraryId, Ct));

        Assert.Empty(await ReservationsAsync(seeded.ServerId));
    }

    [Fact]
    public async Task MovingTheLibraryToAnotherProject_MovesItsPortsToThatProject()
    {
        var seeded = await SeedAsync();
        await using var db = NewContext();
        await AddAsync(seeded, seeded.ProjectLevelLibraryId, db, "PORT_FRONT_GREEN", "10002");
        Assert.Equal($"project:{seeded.ProjectId}", Assert.Single(await ReservationsAsync(seeded.ServerId)).OwnerKey);

        await using (var attach = NewContext())
        {
            attach.ProjectServers.Add(new ProjectServer
            {
                ProjectId = seeded.OtherProjectId,
                ServerId = seeded.ServerId,
                Type = ProjectServerType.ExternalHost,
                DisplayName = "w10-host",
                Host = "w10.local"
            });
            await attach.SaveChangesAsync(Ct);
        }

        await using var move = NewContext();
        var libraries = LibraryPortWiring.Libraries(move, new DbTransactionScope(move));
        var detail = await libraries.GetLibraryDetailAsync(seeded.ProjectLevelLibraryId, Ct);
        await libraries.UpdateLibraryAsync(
            seeded.ProjectLevelLibraryId,
            new UpdateVariableLibraryRequest
            {
                Name = "aetheus.shared",
                Description = "",
                ProjectId = seeded.OtherProjectId,
                RowVersion = detail!.RowVersion
            }, Ct);

        var held = Assert.Single(await ReservationsAsync(seeded.ServerId));
        Assert.Equal($"project:{seeded.OtherProjectId}", held.OwnerKey);
    }

    [Fact]
    public async Task AProjectLevelLibraryReachingSeveralServers_ClaimsNothing_BecauseItsHostIsUnknown()
    {
        var seeded = await SeedAsync();
        await using (var attach = NewContext())
        {
            attach.ProjectServers.Add(new ProjectServer
            {
                ProjectId = seeded.ProjectId,
                ServerId = seeded.OtherServerId,
                Type = ProjectServerType.ExternalHost,
                DisplayName = "second",
                Host = "second.local"
            });
            await attach.SaveChangesAsync(Ct);
        }

        await using var db = NewContext();
        await AddAsync(seeded, seeded.ProjectLevelLibraryId, db, "PORT_FRONT_GREEN", "10002");

        Assert.Empty(await ReservationsAsync(seeded.ServerId));
        Assert.Empty(await ReservationsAsync(seeded.OtherServerId));
    }

    [Fact]
    public async Task R30_ADeployRunOfTheProject_DoesNotReleaseAPortItsOtherLibraryWrites()
    {
        // aetheus.qa writes the nightly's ports; the deploy-prod run of the SAME project on the SAME server
        // declares its own ports at launch and used to release every other declared port of the project,
        // which let the allocator hand 10000 and 10001 to another project again.
        var seeded = await SeedAsync();
        await using (var write = NewContext())
        {
            await AddAsync(seeded, seeded.EnvironmentLibraryId, write, "PORT_FRONT_BLUE", "10000");
            await AddAsync(seeded, seeded.EnvironmentLibraryId, write, "PORT_BACK_BLUE", "10001");
        }

        await using (var launch = NewContext())
        {
            var server = await launch.Servers.AsNoTracking().SingleAsync(candidate => candidate.Id == seeded.ServerId, Ct);
            var dispatch = Substitute.For<IPipelineDispatchServerResolver>();
            dispatch.ResolveServerForTargetAsync(
                    Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(server);
            var guard = LibraryPortWiring.Guard(launch, dispatch);
            var deploy = new PipelineYamlDefinition
            {
                Name = "aetheus-deploy-prod",
                Stages =
                [
                    new PipelineStageDefinition
                    {
                        Name = "Deploy",
                        Agent = "vps",
                        Steps = [new PipelineStepDefinition { Name = "Deploy", Type = "deploy", App = "aetheus" }]
                    }
                ]
            };
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PORT_FRONT"] = "10008",
                ["PORT_BACK"] = "10009"
            };

            await guard.DeclareReservationsAsync(deploy, variables, null, seeded.ProjectId, "Aetheus", Ct);
        }

        Assert.Equal([10000, 10001, 10008, 10009], (await ReservationsAsync(seeded.ServerId)).Select(row => row.Port));

        await using var allocation = NewContext();
        var handed = await LibraryPortWiring.Registry(allocation).AllocateAsync(
            seeded.ServerId, 2, $"project:{seeded.OtherProjectId}", "w10", seeded.OtherProjectId, Ct);
        Assert.Equal([10002, 10003], handed);
    }

    [Fact]
    public async Task Backfill_DeclaresExistingEntries_LeavesAContestedPortWithItsHolder_AndIsIdempotent()
    {
        var seeded = await SeedAsync();
        await using (var holder = NewContext())
        {
            // w10 holds the nightly's first ports, as in production; the library was written before any rule.
            await LibraryPortWiring.Registry(holder).ClaimPortAsync(
                seeded.ServerId, 10000, $"project:{seeded.OtherProjectId}", "w10", seeded.OtherProjectId, Ct);
            holder.VariableLibraryEntries.AddRange(
                new VariableLibraryEntry { VariableLibraryId = seeded.EnvironmentLibraryId, Key = "PORT_FRONT_BLUE", Value = "10000" },
                new VariableLibraryEntry { VariableLibraryId = seeded.EnvironmentLibraryId, Key = "PORT_FRONT_GREEN", Value = "10002" },
                new VariableLibraryEntry { VariableLibraryId = seeded.EnvironmentLibraryId, Key = "PORT_BACK_GREEN", Value = "10005" });
            await holder.SaveChangesAsync(Ct);
        }

        for (var pass = 0; pass < 2; pass++)
        {
            await using var db = NewContext();
            var services = new ServiceCollection()
                .AddSingleton<IPortAllocationRepository>(new PortAllocationRepository(db))
                .AddSingleton(LibraryPortWiring.Registrar(db))
                .BuildServiceProvider();
            await LibraryPortBackfill.RunAsync(services, NullLogger.Instance, Ct);
        }

        var held = await ReservationsAsync(seeded.ServerId);
        Assert.Equal([10000, 10002, 10005], held.Select(row => row.Port));
        Assert.Equal($"project:{seeded.OtherProjectId}", held[0].OwnerKey);
        Assert.Equal($"project:{seeded.ProjectId}", held[1].OwnerKey);
        Assert.Equal($"project:{seeded.ProjectId}", held[2].OwnerKey);
    }
}
