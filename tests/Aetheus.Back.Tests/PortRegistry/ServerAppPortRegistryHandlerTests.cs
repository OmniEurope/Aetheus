// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.ServerApps.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.PortRegistry;

/// <summary>Exercise the event handler with the registry to verify announced application ports are
/// persisted without direct dependencies between peer modules.</summary>
public sealed class ServerAppPortRegistryHandlerTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly PortRegistryService _registry;
    private readonly ServerAppPortRegistryHandler _sut;

    public ServerAppPortRegistryHandlerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options, _clock);
        _db.Servers.Add(new Server { Id = 7, Name = "vps2577917", OrganizationId = 1 });
        _db.SaveChanges();

        _registry = new PortRegistryService(
            new PortRegistryRepository(_db), _clock, NullLogger<PortRegistryService>.Instance);
        _sut = new ServerAppPortRegistryHandler(_registry);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PortSet_LandsInTheRegistryUnderTheApp()
    {
        await _sut.HandleAsync(new ServerAppPortChangedEvent(3, 7, 8080, "grafana"), Ct);

        var row = Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(8080, row.Port);
        Assert.Equal("server-app:3", row.OwnerKey);
        Assert.Equal("grafana", row.OwnerLabel);
        Assert.Equal(PortReservationSource.Manual, row.Source);
    }

    [Fact]
    public async Task PortCleared_ReleasesIt()
    {
        await _sut.HandleAsync(new ServerAppPortChangedEvent(3, 7, 8080, "grafana"), Ct);

        await _sut.HandleAsync(new ServerAppPortChangedEvent(3, null, null, "grafana"), Ct);

        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task PortHeldByAnotherProject_IsRefusedRatherThanTaken()
    {
        await _registry.DeclareAsync(7, [8080], "project:4", "portfolio", 4, Ct);

        // Strictly dispatched by the emitter, so this reaches the caller instead of leaving an app row
        // claiming a port the registry says belongs to somebody else.
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.ConflictException>(() =>
            _sut.HandleAsync(new ServerAppPortChangedEvent(3, 7, 8080, "grafana"), Ct));

        var row = Assert.Single(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("project:4", row.OwnerKey);
    }

    public void Dispose() => _db.Dispose();
}
