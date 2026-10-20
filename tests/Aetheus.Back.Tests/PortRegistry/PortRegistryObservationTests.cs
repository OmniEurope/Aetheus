// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.PortRegistry;

/// <summary>Exercise the repository to verify scans replace observed evidence without modifying
/// project declarations or releasing declared ports.</summary>
public sealed class PortRegistryObservationTests : IDisposable
{
    private static readonly DateTime Origin = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstScan = Origin.AddMinutes(1);
    private static readonly DateTime SecondScan = Origin.AddMinutes(2);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Origin));
    private readonly AppDbContext _db;
    private readonly PortRegistryService _sut;

    public PortRegistryObservationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options, _clock);
        _db.Servers.Add(new Server { Id = 7, Name = "vps2577917", OrganizationId = 1 });
        _db.SaveChanges();
        _sut = new PortRegistryService(
            new PortRegistryRepository(_db), _clock, NullLogger<PortRegistryService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ObservedPortDto Observed(int port, string holder = "nginx") => new()
    {
        Port = port,
        Protocol = "tcp",
        Holder = holder,
        Interface = "0.0.0.0"
    };

    [Fact]
    public async Task ReplaceObservedAsync_RecordsSightingsAndStampsTheServer()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(80), Observed(443)], FirstScan, Ct);

        var rows = await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(PortReservationSource.Observed, row.Source));
        Assert.All(rows, row => Assert.Equal(FirstScan, row.ObservedAt));
        Assert.Equal(FirstScan, (await _db.Servers.AsNoTracking().SingleAsync(s => s.Id == 7, Ct)).PortsObservedAt);
    }

    [Fact]
    public async Task ReplaceObservedAsync_DropsPortsThatStoppedListening()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(80), Observed(443)], FirstScan, Ct);

        await _sut.ReplaceObservedAsync(7, [Observed(80)], SecondScan, Ct);

        var ports = await _db.ServerPortReservations.AsNoTracking().Select(row => row.Port).ToListAsync(Ct);
        Assert.Equal([80], ports);
    }

    [Fact]
    public async Task ReplaceObservedAsync_EmptyScan_ClearsObservationsButKeepsTheStamp()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(80)], FirstScan, Ct);

        await _sut.ReplaceObservedAsync(7, [], SecondScan, Ct);

        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        // Without the stamp the UI could not tell "scanned, nothing listening" from "never scanned".
        Assert.Equal(SecondScan, (await _db.Servers.AsNoTracking().SingleAsync(s => s.Id == 7, Ct)).PortsObservedAt);
    }

    [Fact]
    public async Task ReplaceObservedAsync_NeverTouchesADeclaredClaim()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        // The scan sees nothing at all: the service is stopped, but the project still owns the port.
        await _sut.ReplaceObservedAsync(7, [], FirstScan, Ct);

        var claim = await _db.ServerPortReservations.AsNoTracking()
            .SingleAsync(row => row.Source == PortReservationSource.Declared, Ct);
        Assert.Equal(10031, claim.Port);
        Assert.Equal("portfolio-prod-front", claim.OwnerLabel);
        Assert.Null(claim.ObservedAt);
    }

    [Fact]
    public async Task ReplaceObservedAsync_StampsAConfirmedDeclaredClaim()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        await _sut.ReplaceObservedAsync(7, [Observed(10031, "portfolio-prod-front")], FirstScan, Ct);

        var claim = await _db.ServerPortReservations.AsNoTracking()
            .SingleAsync(row => row.Source == PortReservationSource.Declared, Ct);
        Assert.Equal(FirstScan, claim.ObservedAt);
    }

    [Fact]
    public async Task ReplaceObservedAsync_LaterScanWithoutThePort_KeepsTheOldStampSoItReadsAsStale()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        await _sut.ReplaceObservedAsync(7, [Observed(10031)], FirstScan, Ct);

        await _sut.ReplaceObservedAsync(7, [], SecondScan, Ct);

        var claim = await _db.ServerPortReservations.AsNoTracking()
            .SingleAsync(row => row.Source == PortReservationSource.Declared, Ct);
        // The stamp is kept but no longer equals the server's last scan, which is exactly how the page
        // reads "declared, not observed any more" without losing when it was last seen.
        Assert.Equal(FirstScan, claim.ObservedAt);
        Assert.NotEqual(
            (await _db.Servers.AsNoTracking().SingleAsync(s => s.Id == 7, Ct)).PortsObservedAt,
            claim.ObservedAt);
    }

    [Fact]
    public async Task ReplaceObservedAsync_SamePortTwice_CollapsesToOneRow()
    {
        // IPv4 and IPv6 listeners on the same port are one occupied port, and the (server, port, source)
        // unique index would reject the second row anyway.
        await _sut.ReplaceObservedAsync(7, [Observed(80), Observed(80, "nginx-v6")], FirstScan, Ct);

        var row = Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(80, row.Port);
    }

    [Fact]
    public async Task ReplaceObservedAsync_UnknownServer_WritesNothing()
    {
        await _sut.ReplaceObservedAsync(999, [Observed(80)], FirstScan, Ct);

        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ReplaceObservedAsync_DropsOutOfRangePorts()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(0), Observed(70000), Observed(8080)], FirstScan, Ct);

        var row = Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(8080, row.Port);
    }

    [Fact]
    public async Task ReplaceObservedAsync_CapsTheReport()
    {
        var flood = Enumerable
            .Range(1, PortRegistryLimits.MaxObservedPorts + 50)
            .Select(port => Observed(port))
            .ToList();

        await _sut.ReplaceObservedAsync(7, flood, FirstScan, Ct);

        Assert.Equal(
            PortRegistryLimits.MaxObservedPorts,
            await _db.ServerPortReservations.AsNoTracking().CountAsync(Ct));
    }

    [Fact]
    public async Task GetServerReservationsAsync_ExposesTheObservationStamp()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(80)], FirstScan, Ct);

        var dto = Assert.Single(await _sut.GetServerReservationsAsync(7, Ct));
        Assert.Equal(FirstScan, dto.ObservedAt);
        Assert.Equal(PortReservationSource.Observed, dto.Source);
    }

    // Combined port checks.

    [Fact]
    public async Task CheckPortsAsync_NeverScanned_SaysUnknownInsteadOfFree()
    {
        var result = await _sut.CheckPortsAsync(7, [10041], null, Ct);

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.IsFree);
        Assert.Equal(PortObservationState.NeverScanned, entry.Observation);
        // The verdict must not claim a listener that was never looked for.
        Assert.Equal(PortCheckVerdict.Free, entry.Verdict);
        Assert.Null(result.LastScanAt);
    }

    [Fact]
    public async Task CheckPortsAsync_FreeButListening_IsNotUsableAndNamesWhatHoldsIt()
    {
        // The exact production case: nothing declared it, something outside Aetheus is on it.
        await _sut.ReplaceObservedAsync(7, [Observed(10041, "grafana")], FirstScan, Ct);

        var entry = Assert.Single((await _sut.CheckPortsAsync(7, [10041], null, Ct)).Entries);

        Assert.True(entry.IsFree);
        Assert.Equal(PortObservationState.Listening, entry.Observation);
        Assert.Equal("grafana", entry.ObservedHolder);
        Assert.Equal(PortCheckVerdict.ListeningUndeclared, entry.Verdict);
    }

    [Fact]
    public async Task CheckPortsAsync_DeclaredButNotListening_StaysTakenAndSaysSo()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        await _sut.ReplaceObservedAsync(7, [], FirstScan, Ct);

        var entry = Assert.Single((await _sut.CheckPortsAsync(7, [10031], null, Ct)).Entries);

        Assert.False(entry.IsFree);
        Assert.Equal("portfolio-prod-front", entry.OwnerLabel);
        Assert.Equal(PortObservationState.NotListening, entry.Observation);
        Assert.Equal(PortCheckVerdict.TakenByAnotherOwner, entry.Verdict);
    }

    [Fact]
    public async Task CheckPortsAsync_DeclaredAndListening_ReportsBothFacts()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        await _sut.ReplaceObservedAsync(7, [Observed(10031, "portfolio-prod-front")], FirstScan, Ct);

        var entry = Assert.Single((await _sut.CheckPortsAsync(7, [10031], null, Ct)).Entries);

        Assert.False(entry.IsFree);
        Assert.Equal(PortObservationState.Listening, entry.Observation);
        // No context project here, so the holder is somebody else as far as this caller knows.
        Assert.Equal(PortCheckVerdict.TakenByAnotherOwner, entry.Verdict);
    }

    [Fact]
    public async Task CheckPortsAsync_FreeAndNotListening_IsTheOnlyUsableVerdict()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(80)], FirstScan, Ct);

        var entry = Assert.Single((await _sut.CheckPortsAsync(7, [10041], null, Ct)).Entries);

        Assert.True(entry.IsFree);
        Assert.Equal(PortObservationState.NotListening, entry.Observation);
        Assert.Equal(PortCheckVerdict.Free, entry.Verdict);
        Assert.Equal(FirstScan, (await _sut.CheckPortsAsync(7, [10041], null, Ct)).LastScanAt);
    }

    [Fact]
    public async Task CheckPortsAsync_DeclaredByTheAskingProjectAndListening_ReadsAsItsOwn()
    {
        // Evaluate a declared listening port in the requesting project context so its own
        // deployment is distinguished from another owner.
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        await _sut.ReplaceObservedAsync(7, [Observed(10031, "portfolio-prod-front")], FirstScan, Ct);

        var mine = Assert.Single((await _sut.CheckPortsAsync(7, [10031], 4, Ct)).Entries);
        Assert.Equal(PortCheckVerdict.UsedByThisProject, mine.Verdict);

        // The same port, asked about by a different project, is an obstacle.
        var theirs = Assert.Single((await _sut.CheckPortsAsync(7, [10031], 9, Ct)).Entries);
        Assert.Equal(PortCheckVerdict.TakenByAnotherOwner, theirs.Verdict);
        Assert.Equal("portfolio-prod-front", theirs.OwnerLabel);
    }

    [Fact]
    public async Task CheckPortsAsync_ReportsWhetherTheAgentCanScan()
    {
        var server = await _db.Servers.SingleAsync(candidate => candidate.Id == 7, Ct);
        server.PortObservationAvailable = true;
        await _db.SaveChangesAsync(Ct);

        Assert.True((await _sut.CheckPortsAsync(7, [10041], null, Ct)).ObservationAvailable);
    }

    // --- The conflict rule, now that observations exist ---

    [Fact]
    public async Task FindConflictsAsync_OwnRunningService_IsNotAConflictWithItself()
    {
        // A redeployment: the project holds 10031 and its own container is listening on it. Comparing
        // rows one by one would make the observation (owner key "observed") block the redeploy.
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        await _sut.ReplaceObservedAsync(7, [Observed(10031, "portfolio-prod-front")], FirstScan, Ct);

        var conflicts = await _sut.FindConflictsAsync(7, [10031], "project:4", Ct);

        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task FindConflictsAsync_ForeignDeclaredPort_NamesTheDeclaredHolderNotTheProxy()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        await _sut.ReplaceObservedAsync(7, [Observed(10031, "docker-proxy")], FirstScan, Ct);

        var conflict = Assert.Single(await _sut.FindConflictsAsync(7, [10031], "project:9", Ct));

        Assert.Equal("portfolio-prod-front", conflict.OwnerLabel);
        Assert.Equal(PortReservationSource.Declared, conflict.Source);
    }

    [Fact]
    public async Task FindConflictsAsync_ObservedOnlyPort_BlocksAndNamesWhatIsThere()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(9000, "grafana")], FirstScan, Ct);

        var conflict = Assert.Single(await _sut.FindConflictsAsync(7, [9000], "project:4", Ct));

        Assert.Equal("grafana", conflict.OwnerLabel);
        Assert.Equal(PortReservationSource.Observed, conflict.Source);
    }

    [Fact]
    public async Task AddManualReservationAsync_OnAnObservedPort_IsAllowed()
    {
        // The "Reserve" action on an observed listener: its own sighting must not refuse it.
        await _sut.ReplaceObservedAsync(7, [Observed(9000, "grafana")], FirstScan, Ct);

        var created = await _sut.AddManualReservationAsync(
            7, new CreatePortReservationRequest { Port = 9000, OwnerLabel = "grafana" }, Ct);

        Assert.Equal(PortReservationSource.Manual, created.Source);
        Assert.Equal(2, await _db.ServerPortReservations.CountAsync(row => row.Port == 9000, Ct));
    }

    [Fact]
    public async Task AddManualReservationAsync_OnADeclaredPort_IsStillRefused()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.ConflictException>(() =>
            _sut.AddManualReservationAsync(
                7, new CreatePortReservationRequest { Port = 10031, OwnerLabel = "moi" }, Ct));
    }

    // Port allocation.

    [Fact]
    public async Task GetPortRangeAsync_WithoutARow_ReportsTheFleetDefaultAsSuch()
    {
        var range = await _sut.GetPortRangeAsync(7, Ct);

        Assert.Equal(PortRegistryLimits.DefaultRangeFrom, range.From);
        Assert.Equal(PortRegistryLimits.DefaultRangeTo, range.To);
        // The caller must be able to tell an inherited default from a decision made on this server.
        Assert.False(range.IsExplicit);
    }

    [Fact]
    public async Task SetPortRangeAsync_StoresThenUpdatesOneRowPerServer()
    {
        await _sut.SetPortRangeAsync(7, new PortRangeDto { From = 30000, To = 30010 }, Ct);
        await _sut.SetPortRangeAsync(7, new PortRangeDto { From = 30000, To = 30020 }, Ct);

        var range = await _sut.GetPortRangeAsync(7, Ct);
        Assert.Equal(30020, range.To);
        Assert.True(range.IsExplicit);
        Assert.Equal(1, await _db.ServerPortRanges.CountAsync(row => row.ServerId == 7, Ct));
    }

    [Theory]
    [InlineData(80, 90)]
    [InlineData(30000, 29000)]
    public async Task SetPortRangeAsync_RefusesAnUnusableWindow(int from, int to)
    {
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() =>
            _sut.SetPortRangeAsync(7, new PortRangeDto { From = from, To = to }, Ct));
    }

    [Fact]
    public async Task AllocateAsync_HandsOutTheFirstFreePortsAndReservesThem()
    {
        await _sut.SetPortRangeAsync(7, new PortRangeDto { From = 10000, To = 10010 }, Ct);

        var ports = await _sut.AllocateAsync(7, 3, "project:4", "portfolio", 4, Ct);

        Assert.Equal([10000, 10001, 10002], ports);
        var rows = await _db.ServerPortReservations.AsNoTracking()
            .Where(row => row.OwnerKey == "project:4").ToListAsync(Ct);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(PortReservationSource.Declared, row.Source));
        Assert.All(rows, row => Assert.Equal(4, row.ProjectId));
    }

    [Fact]
    public async Task AllocateAsync_SkipsTakenAndObservedPorts()
    {
        await _sut.SetPortRangeAsync(7, new PortRangeDto { From = 10000, To = 10010 }, Ct);
        await _sut.DeclareAsync(7, [10000], "project:9", "autre", 9, Ct);
        // 10001 is bound by something nobody declared: handing it out would produce the exact bind
        // failure the registry exists to prevent.
        await _sut.ReplaceObservedAsync(7, [Observed(10001, "grafana")], FirstScan, Ct);

        var ports = await _sut.AllocateAsync(7, 2, "project:4", "portfolio", 4, Ct);

        Assert.Equal([10002, 10003], ports);
    }

    [Fact]
    public async Task AllocateAsync_NeverHandsOutAPrivilegedPort()
    {
        // The window is stored below the floor, bypassing the setter's own validation, so the check
        // being proved is the allocator's and not the setter's.
        _db.ServerPortRanges.Add(new ServerPortRange
        {
            ServerId = 7,
            From = 1020,
            To = 1026,
            UpdatedAt = Origin
        });
        await _db.SaveChangesAsync(Ct);

        var ports = await _sut.AllocateAsync(7, 3, "project:4", "portfolio", 4, Ct);

        Assert.Equal([1024, 1025, 1026], ports);
        Assert.All(ports, port => Assert.True(port >= PortRegistryLimits.MinAllocatablePort));
    }

    [Fact]
    public async Task AllocateAsync_TooSmallAWindow_SaysHowManyRemain()
    {
        await _sut.SetPortRangeAsync(7, new PortRangeDto { From = 10000, To = 10001 }, Ct);

        var error = await Assert.ThrowsAsync<Aetheus.Back.Exceptions.ConflictException>(() =>
            _sut.AllocateAsync(7, 5, "project:4", "portfolio", 4, Ct));

        Assert.Contains("only 2 free port(s)", error.Message, StringComparison.Ordinal);
        // Nothing is reserved when the whole request cannot be satisfied.
        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task AllocateAsync_TwiceForTheSameOwner_AddsPortsInsteadOfReleasingTheFirstOnes()
    {
        // DeclareAsync releases what its owner no longer asks for; allocation must not, or the second
        // request would free the ports the first deployment is already using.
        await _sut.SetPortRangeAsync(7, new PortRangeDto { From = 10000, To = 10010 }, Ct);
        var first = await _sut.AllocateAsync(7, 2, "project:4", "portfolio", 4, Ct);

        var second = await _sut.AllocateAsync(7, 2, "project:4", "portfolio", 4, Ct);

        Assert.Equal([10000, 10001], first);
        Assert.Equal([10002, 10003], second);
        Assert.Equal(4, await _db.ServerPortReservations.CountAsync(row => row.OwnerKey == "project:4", Ct));
    }

    [Fact]
    public async Task AllocateAsync_UnknownServer_IsNotFound()
    {
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.NotFoundException>(() =>
            _sut.AllocateAsync(999, 1, "project:4", "portfolio", 4, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PortRegistryLimits.MaxPortsPerAllocation + 1)]
    public async Task AllocateAsync_RefusesAnAbsurdCount(int count)
    {
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() =>
            _sut.AllocateAsync(7, count, "project:4", "portfolio", 4, Ct));
    }

    // Conflict rejection at the registry barrier.

    [Fact]
    public async Task DeclareAsync_OnAPortDeclaredByAnother_ThrowsInsteadOfLogging()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        var error = await Assert.ThrowsAsync<Aetheus.Back.Exceptions.ConflictException>(() =>
            _sut.DeclareAsync(7, [10031], "project:9", "autre-projet", 9, Ct));

        Assert.Contains("portfolio-prod-front", error.Message, StringComparison.Ordinal);
        // The holder is untouched: a refused declaration must not half-write.
        var row = await _db.ServerPortReservations.AsNoTracking()
            .SingleAsync(reservation => reservation.Port == 10031, Ct);
        Assert.Equal("project:4", row.OwnerKey);
    }

    [Fact]
    public async Task DeclareAsync_ReDeclaringItsOwnPorts_IsStillIdempotent()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        Assert.Equal(1, await _db.ServerPortReservations.CountAsync(row => row.Port == 10031, Ct));
    }

    // Project deletion releases its ports.

    [Fact]
    public async Task ReleaseProjectPortsAsync_DropsEveryPortThatProjectHeld()
    {
        _db.Servers.Add(new Server { Id = 9, Name = "vps-third", OrganizationId = 1 });
        await _db.SaveChangesAsync(Ct);
        await _sut.DeclareAsync(7, [10031, 10032], "project:4", "portfolio", 4, Ct);
        await _sut.DeclareAsync(9, [10041], "project:4", "portfolio", 4, Ct);
        await _sut.DeclareAsync(7, [10051], "project:9", "autre", 9, Ct);

        var released = await _sut.ReleaseProjectPortsAsync(4, Ct);

        Assert.Equal(3, released);
        // The other project keeps everything: deleting one project frees only its own ports.
        var left = await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct);
        Assert.Equal([10051], left.Select(row => row.Port));
    }

    [Fact]
    public async Task ReleaseProjectPortsAsync_LeavesObservationsAlone()
    {
        // Deleting a project does not stop its container from listening; the scan owns that row.
        await _sut.ReplaceObservedAsync(7, [Observed(10031, "portfolio-front")], FirstScan, Ct);

        Assert.Equal(0, await _sut.ReleaseProjectPortsAsync(4, Ct));
        Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ReleaseProjectPortsAsync_ProjectWithNoPorts_IsANoOp()
    {
        Assert.Equal(0, await _sut.ReleaseProjectPortsAsync(4, Ct));
    }

    // Project and server-application port fields are reflected in the registry.

    [Fact]
    public async Task SyncOwnedPortAsync_RecordsTheOwnersPortAsAManualReservation()
    {
        await _sut.SyncOwnedPortAsync(7, 8080, "server-app:3", "grafana", null, Ct);

        var row = Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(8080, row.Port);
        Assert.Equal("server-app:3", row.OwnerKey);
        Assert.Equal(PortReservationSource.Manual, row.Source);
    }

    [Fact]
    public async Task SyncOwnedPortAsync_IsIdempotent()
    {
        await _sut.SyncOwnedPortAsync(7, 8080, "server-app:3", "grafana", null, Ct);
        await _sut.SyncOwnedPortAsync(7, 8080, "server-app:3", "grafana renamed", null, Ct);

        var row = Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("grafana renamed", row.OwnerLabel);
    }

    [Fact]
    public async Task SyncOwnedPortAsync_MovingThePort_LeavesNothingBehind()
    {
        await _sut.SyncOwnedPortAsync(7, 8080, "server-app:3", "grafana", null, Ct);

        await _sut.SyncOwnedPortAsync(7, 9090, "server-app:3", "grafana", null, Ct);

        var row = Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(9090, row.Port);
    }

    [Fact]
    public async Task SyncOwnedPortAsync_ClearingThePort_ReleasesIt()
    {
        await _sut.SyncOwnedPortAsync(7, 8080, "server-app:3", "grafana", null, Ct);

        await _sut.SyncOwnedPortAsync(7, null, "server-app:3", "grafana", null, Ct);

        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task SyncOwnedPortAsync_WithoutAServer_ClaimsNothing()
    {
        // An external host names a machine Aetheus does not manage: its port has nowhere to be recorded.
        await _sut.SyncOwnedPortAsync(null, 8080, "project-server:5", "vitrine", 4, Ct);

        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task SyncOwnedPortAsync_OnSomebodyElsesPort_IsRefused()
    {
        await _sut.DeclareAsync(7, [8080], "project:4", "portfolio", 4, Ct);

        var error = await Assert.ThrowsAsync<Aetheus.Back.Exceptions.ConflictException>(() =>
            _sut.SyncOwnedPortAsync(7, 8080, "server-app:3", "grafana", null, Ct));

        Assert.Contains("portfolio", error.Message, StringComparison.Ordinal);
        // The holder is untouched: a descriptor cannot take a port the registry already defends.
        var row = Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("project:4", row.OwnerKey);
    }

    [Fact]
    public async Task SyncOwnedPortAsync_OnAnObservedPort_IsAllowed()
    {
        // Recording what an app actually holds is the point; its own sighting must not refuse it.
        await _sut.ReplaceObservedAsync(7, [Observed(8080, "grafana")], FirstScan, Ct);

        await _sut.SyncOwnedPortAsync(7, 8080, "server-app:3", "grafana", null, Ct);

        Assert.Equal(2, await _db.ServerPortReservations.CountAsync(row => row.Port == 8080, Ct));
    }

    [Fact]
    public async Task SyncedPort_ThenBlocksAnotherProjectsLaunch()
    {
        // The whole point of carrying these fields in: a port an app holds is now visible to the
        // conflict rule instead of being a field nothing else could read.
        await _sut.SyncOwnedPortAsync(7, 8080, "server-app:3", "grafana", null, Ct);

        var conflict = Assert.Single(await _sut.FindConflictsAsync(7, [8080], "project:4", Ct));

        Assert.Equal("grafana", conflict.OwnerLabel);
    }

    private static ObservedPortDto ObservedUdp(int port, string holder = "dnsmasq") =>
        Observed(port, holder) with { Protocol = "udp" };

    [Fact]
    public async Task ReplaceObservedAsync_KeepsTheSameNumberOnBothProtocols()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(53, "nginx"), ObservedUdp(53)], FirstScan, Ct);

        var rows = await _db.ServerPortReservations.AsNoTracking()
            .Where(row => row.Port == 53).ToListAsync(Ct);

        // Two occupied ports, not one: collapsing them would hide whichever arrived second.
        Assert.Equal(2, rows.Count);
        Assert.Equal("nginx", Assert.Single(rows, row => row.Protocol == "tcp").OwnerLabel);
        Assert.Equal("dnsmasq", Assert.Single(rows, row => row.Protocol == "udp").OwnerLabel);
    }

    [Fact]
    public async Task UdpSighting_NeverRefusesATcpDeployment()
    {
        await _sut.ReplaceObservedAsync(7, [ObservedUdp(10041)], FirstScan, Ct);

        // The regression this column exists to prevent: a DNS resolver on 10041/udp must not make
        // 10041/tcp unusable, since the two stacks bind independently.
        Assert.Empty(await _sut.FindConflictsAsync(7, [10041], "project:4", Ct));

        var check = Assert.Single((await _sut.CheckPortsAsync(7, [10041], null, Ct)).Entries);
        Assert.Equal(PortCheckVerdict.Free, check.Verdict);
        Assert.Equal(PortObservationState.NotListening, check.Observation);

        // Allocation reads the same fact: a UDP row must not burn the only port in the window.
        await _sut.SetPortRangeAsync(7, new PortRangeDto { From = 10041, To = 10041 }, Ct);
        Assert.Equal([10041], await _sut.AllocateAsync(7, 1, "project:4", "portfolio", 4, Ct));
    }

    [Fact]
    public async Task UdpSighting_DoesNotConfirmATcpClaim()
    {
        await _sut.DeclareAsync(7, [10041], "project:4", "portfolio", 4, Ct);
        await _sut.ReplaceObservedAsync(7, [ObservedUdp(10041)], FirstScan, Ct);

        // "Declared AND listening" is a TCP fact; a UDP socket on the number proves nothing about it.
        var claim = await _db.ServerPortReservations.AsNoTracking()
            .SingleAsync(row => row.Port == 10041 && row.Source == PortReservationSource.Declared, Ct);
        Assert.Null(claim.ObservedAt);
    }

    public void Dispose() => _db.Dispose();
}
