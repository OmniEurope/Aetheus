// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.PortRegistry;

/// <summary>Exercise reservation ownership and validation, rejecting libraries without projects and
/// keys without PORT_ because the pipeline guard cannot protect them.</summary>
public sealed class PortAllocationServiceTests : IDisposable
{
    private static readonly DateTime Origin = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Origin));
    private readonly AppDbContext _db;
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly PortRegistryService _registry;
    private readonly PortAllocationService _sut;

    public PortAllocationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options, _clock);

        _db.Organizations.Add(new Organization { Id = 1, Name = "Org", Slug = "org" });
        _db.Servers.Add(new Server { Id = 7, Name = "vps2577917", OrganizationId = 1 });
        _db.Servers.Add(new Server { Id = 8, Name = "vps-other", OrganizationId = 1 });
        _db.Projects.Add(new Project { Id = 4, Name = "Portfolio", OrganizationId = 1 });
        _db.ProjectServers.Add(new ProjectServer { Id = 40, ProjectId = 4, ServerId = 7, DisplayName = "prod" });
        _db.Environments.Add(new Data.Entities.Environment { Id = 60, Name = "prod", ProjectId = 4 });
        _db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 60, ServerId = 7 });
        // An environment attached to no project: the case allocation must refuse.
        _db.Environments.Add(new Data.Entities.Environment { Id = 61, Name = "shared", ProjectId = null });
        _db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 61, ServerId = 8 });

        _db.VariableLibraries.Add(new VariableLibrary { Id = 100, Name = "portfolio-host", ProjectId = 4 });
        _db.VariableLibraries.Add(new VariableLibrary { Id = 101, Name = "prod-host", EnvironmentId = 60 });
        _db.VariableLibraries.Add(new VariableLibrary { Id = 102, Name = "server-host", ProjectServerId = 40 });
        _db.VariableLibraries.Add(new VariableLibrary { Id = 103, Name = "orphan-host", EnvironmentId = 61 });
        _db.SaveChanges();

        _registry = new PortRegistryService(
            new PortRegistryRepository(_db), _clock, NullLogger<PortRegistryService>.Instance);
        _sut = new PortAllocationService(
            new PortAllocationRepository(_db),
            _registry,
            _libraries,
            new NoTransactionScope(),
            NullLogger<PortAllocationService>.Instance);

        _libraries
            .CreateEntryAsync(Arg.Any<int>(), Arg.Any<CreateVariableEntryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<CreateVariableEntryRequest>();
                var entry = new VariableLibraryEntry
                {
                    VariableLibraryId = call.Arg<int>(),
                    Key = request.Key,
                    Value = request.Value
                };
                _db.VariableLibraryEntries.Add(entry);
                _db.SaveChanges();
                return new VariableEntryDto { Id = entry.Id, Key = entry.Key, Value = entry.Value };
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The InMemory provider has no transactions, exactly as in the app: the service branches on
    /// <see cref="IDbTransactionScope.IsRelational"/> and runs the same body without one, so what these
    /// tests exercise is the compensation. The transactional path is covered on real PostgreSQL by
    /// <c>PortAllocationTransactionIntegrationTests</c>.
    /// </summary>
    private sealed class NoTransactionScope : IDbTransactionScope
    {
        public bool IsRelational => false;

        public bool InTransaction => false;

        public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task GetTargetsAsync_ProjectScope_OffersItsServersWithoutPreselecting()
    {
        var targets = await _sut.GetTargetsAsync(100, Ct);

        Assert.Equal(4, targets.ProjectId);
        Assert.Equal("vps2577917", Assert.Single(targets.Servers).ServerName);
        // A project reaching one server today may reach two tomorrow, so it still asks.
        Assert.Null(targets.PreselectedServerId);
        Assert.Equal(PortAllocationBlocker.None, targets.Blocker);
    }

    [Fact]
    public async Task GetTargetsAsync_ProjectServerScope_PreselectsItsOwnServer()
    {
        var targets = await _sut.GetTargetsAsync(102, Ct);

        Assert.Equal(7, targets.PreselectedServerId);
        Assert.Equal(4, targets.ProjectId);
    }

    [Fact]
    public async Task GetTargetsAsync_EnvironmentWithOneServer_PreselectsIt()
    {
        var targets = await _sut.GetTargetsAsync(101, Ct);

        Assert.Equal(7, targets.PreselectedServerId);
    }

    [Fact]
    public async Task GetTargetsAsync_LibraryWithoutAProject_SaysWhyItCannotAllocate()
    {
        var targets = await _sut.GetTargetsAsync(103, Ct);

        Assert.Null(targets.ProjectId);
        // A code, not a sentence: the UI localizes it instead of showing a server-side English string.
        Assert.Equal(PortAllocationBlocker.NoProject, targets.Blocker);
    }

    [Fact]
    public async Task GetTargetsAsync_UnknownLibrary_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetTargetsAsync(999, Ct));
    }

    [Fact]
    public async Task AllocateIntoLibraryAsync_ReservesUnderTheProjectAndWritesTheEntries()
    {
        var created = await _sut.AllocateIntoLibraryAsync(100, 7, ["PORT_FRONT", "PORT_BACK"], Ct);

        Assert.Equal(["PORT_FRONT", "PORT_BACK"], created.Select(entry => entry.Key));
        Assert.Equal(["10000", "10001"], created.Select(entry => entry.Value));

        var reservations = await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(2, reservations.Count);
        // Under the PROJECT key: a manual key would make this very allocation block the project's own
        // pipeline at its next launch.
        Assert.All(reservations, row => Assert.Equal("project:4", row.OwnerKey));
        Assert.All(reservations, row => Assert.Equal(4, row.ProjectId));
    }

    [Fact]
    public async Task AllocateIntoLibraryAsync_ThenTheProjectDeploys_IsNotBlockedByItsOwnPorts()
    {
        var created = await _sut.AllocateIntoLibraryAsync(100, 7, ["PORT_FRONT"], Ct);
        var port = int.Parse(created[0].Value, System.Globalization.CultureInfo.InvariantCulture);

        var conflicts = await _registry.FindConflictsAsync(7, [port], "project:4", Ct);

        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task AllocateIntoLibraryAsync_LibraryWithoutAProject_IsRefusedAndWritesNothing()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.AllocateIntoLibraryAsync(103, 8, ["PORT_FRONT"], Ct));

        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        await _libraries.DidNotReceive().CreateEntryAsync(
            Arg.Any<int>(), Arg.Any<CreateVariableEntryRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("FRONT_PORT")]
    [InlineData("port_front")]
    [InlineData("FRONT")]
    public async Task AllocateIntoLibraryAsync_KeyWithoutThePrefix_IsRefused(string key)
    {
        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.AllocateIntoLibraryAsync(100, 7, [key], Ct));

        Assert.Contains(PortRegistryLimits.PortKeyPrefix, error.Message, StringComparison.Ordinal);
        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task AllocateIntoLibraryAsync_DuplicateKey_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.AllocateIntoLibraryAsync(100, 7, ["PORT_FRONT", "PORT_FRONT"], Ct));
    }

    [Fact]
    public async Task AllocateIntoLibraryAsync_NoKeys_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.AllocateIntoLibraryAsync(100, 7, [" ", string.Empty], Ct));
    }

    [Fact]
    public async Task AllocateIntoLibraryAsync_UnknownServer_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.AllocateIntoLibraryAsync(100, 999, ["PORT_FRONT"], Ct));
    }

    [Fact]
    public async Task CheckLibraryPortsAsync_ReadsTheLibrarysOwnPortEntries()
    {
        _db.VariableLibraryEntries.AddRange(
            new VariableLibraryEntry { VariableLibraryId = 100, Key = "PORT_FRONT", Value = "10031" },
            new VariableLibraryEntry { VariableLibraryId = 100, Key = "PORT_BACK", Value = "10041" },
            // Neither of these is a port entry to check.
            new VariableLibraryEntry { VariableLibraryId = 100, Key = "DB_HOST", Value = "localhost" },
            new VariableLibraryEntry { VariableLibraryId = 100, Key = "PORT_LABEL", Value = "not-a-port" });
        await _db.SaveChangesAsync(Ct);
        await _registry.DeclareAsync(7, [10031], "project:9", "autre", 9, Ct);

        var result = await _sut.CheckLibraryPortsAsync(100, 7, Ct);

        Assert.Equal([10031, 10041], result.Entries.Select(entry => entry.Port));
        Assert.False(result.Entries[0].IsFree);
        Assert.True(result.Entries[1].IsFree);
    }

    [Fact]
    public async Task CheckLibraryPortsAsync_JudgesEachPortRelativeToTheLibrarysProject()
    {
        // Pass the library project into port checks to distinguish its own declaration from another
        // project reservation.
        _db.VariableLibraryEntries.AddRange(
            new VariableLibraryEntry { VariableLibraryId = 100, Key = "PORT_FRONT", Value = "10031" },
            new VariableLibraryEntry { VariableLibraryId = 100, Key = "PORT_BACK", Value = "10041" });
        await _db.SaveChangesAsync(Ct);
        await _registry.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        await _registry.DeclareAsync(7, [10041], "project:9", "autre", 9, Ct);

        var result = await _sut.CheckLibraryPortsAsync(100, 7, Ct);

        Assert.Equal(PortCheckVerdict.UsedByThisProject, result.Entries.Single(entry => entry.Port == 10031).Verdict);
        Assert.Equal(PortCheckVerdict.TakenByAnotherOwner, result.Entries.Single(entry => entry.Port == 10041).Verdict);
    }

    [Fact]
    public async Task CheckLibraryPortsAsync_LibraryWithoutPortEntries_SaysSo()
    {
        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CheckLibraryPortsAsync(100, 7, Ct));

        Assert.Contains(PortRegistryLimits.PortKeyPrefix, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllocateIntoLibraryAsync_WhenWritingAnEntryFails_ReleasesWhatItHadReserved()
    {
        // The compensation that replaces a transaction: without it, a failed allocation would leave
        // reserved ports blocking the server with nothing using them.
        _libraries
            .CreateEntryAsync(Arg.Any<int>(), Arg.Any<CreateVariableEntryRequest>(), Arg.Any<CancellationToken>())
            .Returns<VariableEntryDto>(_ => throw new InvalidOperationException("entry rejected"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.AllocateIntoLibraryAsync(100, 7, ["PORT_FRONT", "PORT_BACK"], Ct));

        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task AllocateIntoLibraryAsync_WhenTheSecondEntryFails_ReleasesBothPorts()
    {
        var calls = 0;
        _libraries
            .CreateEntryAsync(Arg.Any<int>(), Arg.Any<CreateVariableEntryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (++calls == 2) throw new InvalidOperationException("entry rejected");
                var request = call.Arg<CreateVariableEntryRequest>();
                return new VariableEntryDto { Id = calls, Key = request.Key, Value = request.Value };
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.AllocateIntoLibraryAsync(100, 7, ["PORT_FRONT", "PORT_BACK"], Ct));

        // Both, not just the one whose write failed: the whole allocation is undone.
        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ReleaseLibraryPortAsync_ReleasesWhatItsOwnProjectHolds()
    {
        var created = await _sut.AllocateIntoLibraryAsync(100, 7, ["PORT_FRONT"], Ct);
        var port = int.Parse(created[0].Value, System.Globalization.CultureInfo.InvariantCulture);

        var released = await _sut.ReleaseLibraryPortAsync(100, port, Ct);

        Assert.Equal(1, released);
        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ReleaseLibraryPortAsync_LeavesAnotherHoldersPortAlone()
    {
        // Deleting a variable must never free somebody else's port: that release is a deliberate act on
        // the registry page, gated on writing the server.
        await _registry.DeclareAsync(7, [10031], "project:9", "autre-projet", 9, Ct);

        var released = await _sut.ReleaseLibraryPortAsync(100, 10031, Ct);

        Assert.Equal(0, released);
        Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ReleaseLibraryPortAsync_LibraryWithoutAProject_ReleasesNothing()
    {
        Assert.Equal(0, await _sut.ReleaseLibraryPortAsync(103, 10031, Ct));
    }

    [Fact]
    public async Task ReleaseLibraryPortAsync_NeverTouchesAnObservation()
    {
        // An observation says what the host shows; deleting it would only make it come back at the next
        // scan while pretending the port was freed.
        await _registry.ReplaceObservedAsync(
            7,
            [new ObservedPortDto { Port = 10031, Protocol = "tcp", Holder = "grafana", Interface = "0.0.0.0" }],
            Origin,
            Ct);

        var released = await _sut.ReleaseLibraryPortAsync(100, 10031, Ct);

        Assert.Equal(0, released);
        Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    public void Dispose() => _db.Dispose();
}
