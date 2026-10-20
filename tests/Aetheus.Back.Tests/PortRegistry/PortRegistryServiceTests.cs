// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.PortRegistry;

/// <summary>
/// The registry over the real repository (EF InMemory), not a mock: the value of this feature is
/// whether "10031 is taken by portfolio-prod-front" comes back correctly from storage, which a
/// substituted repository would assert nothing about.
/// </summary>
public sealed class PortRegistryServiceTests : IDisposable
{
    private static readonly DateTime Origin = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Origin));
    private readonly AppDbContext _db;
    private readonly PortRegistryService _sut;

    public PortRegistryServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options, _clock);
        _db.Servers.Add(new Server { Id = 7, Name = "vps2577917", OrganizationId = 1 });
        _db.Servers.Add(new Server { Id = 8, Name = "vps-other", OrganizationId = 1 });
        _db.SaveChanges();
        _sut = new PortRegistryService(
            new PortRegistryRepository(_db), _clock, NullLogger<PortRegistryService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckPortsAsync_ReportsFreeAndTaken_WithTheHoldersName()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        var result = await _sut.CheckPortsAsync(7, [10031, 10041], null, Ct);

        Assert.Equal("vps2577917", result.ServerName);
        Assert.Equal(2, result.Entries.Count);
        var taken = result.Entries.Single(entry => entry.Port == 10031);
        Assert.False(taken.IsFree);
        Assert.Equal("portfolio-prod-front", taken.OwnerLabel);
        Assert.Equal(PortReservationSource.Declared, taken.Source);
        Assert.True(result.Entries.Single(entry => entry.Port == 10041).IsFree);
    }

    [Fact]
    public async Task CheckPortsAsync_IsScopedToOneServer()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        var elsewhere = await _sut.CheckPortsAsync(8, [10031], null, Ct);

        Assert.True(elsewhere.Entries.Single().IsFree);
    }

    [Fact]
    public async Task CheckPortsAsync_Throws_ForAnUnknownServer()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.CheckPortsAsync(999, [10031], null, Ct));
    }

    [Fact]
    public async Task FindConflictsAsync_ReportsAPortHeldByAnotherOwner()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        var conflicts = await _sut.FindConflictsAsync(7, [10031, 10032], "project:9", Ct);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(10031, conflict.Port);
        Assert.Equal("portfolio-prod-front", conflict.OwnerLabel);
    }

    [Fact]
    public async Task FindConflictsAsync_IsSilent_ForTheSameOwner()
    {
        await _sut.DeclareAsync(7, [10031, 10032], "project:4", "portfolio-prod-front", 4, Ct);

        Assert.Empty(await _sut.FindConflictsAsync(7, [10031, 10032], "project:4", Ct));
    }

    [Fact]
    public async Task DeclareAsync_IsIdempotent_AndRefreshesTheTimestamp()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        _clock.SetUtcNow(new DateTimeOffset(Origin.AddHours(3)));
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        var reservation = Assert.Single(await _sut.GetServerReservationsAsync(7, Ct));
        Assert.Equal(Origin, reservation.DeclaredAt);
        Assert.Equal(Origin.AddHours(3), reservation.UpdatedAt);
    }

    [Fact]
    public async Task DeclareAsync_ReleasesThePortsTheOwnerNoLongerAsksFor()
    {
        await _sut.DeclareAsync(7, [10031, 10032], "project:4", "portfolio", 4, Ct);
        await _sut.DeclareAsync(7, [10041, 10042], "project:4", "portfolio", 4, Ct);

        var ports = (await _sut.GetServerReservationsAsync(7, Ct)).Select(item => item.Port).ToList();
        Assert.Equal([10041, 10042], ports);
    }

    [Fact]
    public async Task DeclareAsync_NeverStealsAPortHeldByAnotherOwner()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        // Refuse a declaration that loses the conflict race instead of allowing a run without its
        // reservation.
        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.DeclareAsync(7, [10031], "project:9", "aetheus-demo", 9, Ct));

        var reservation = Assert.Single(await _sut.GetServerReservationsAsync(7, Ct));
        Assert.Equal("portfolio-prod-front", reservation.OwnerLabel);
    }

    [Fact]
    public async Task AddManualReservationAsync_RefusesAPortAlreadyRegistered()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.AddManualReservationAsync(
            7, new CreatePortReservationRequest { Port = 10031, OwnerLabel = "legacy-daemon" }, Ct));
    }

    [Fact]
    public async Task ReleaseAsync_RemovesTheRow_AndIgnoresAnotherServersId()
    {
        var manual = await _sut.AddManualReservationAsync(
            7, new CreatePortReservationRequest { Port = 10031, OwnerLabel = "legacy-daemon" }, Ct);

        Assert.False(await _sut.ReleaseAsync(8, manual.Id, Ct));
        Assert.True(await _sut.ReleaseAsync(7, manual.Id, Ct));
        Assert.Empty(await _sut.GetServerReservationsAsync(7, Ct));
    }

    public void Dispose() => _db.Dispose();
}
