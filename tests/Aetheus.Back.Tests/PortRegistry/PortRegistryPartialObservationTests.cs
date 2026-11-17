// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.PortRegistry;

/// <summary>
/// ACORE-003: a scan whose probe failed for one protocol must not read as a complete scan. Only the
/// protocols the agent measured are replaced, each dated by its own measurement, and what was not measured
/// is kept as it was (declared and manual claims are never touched either way).
/// </summary>
public sealed class PortRegistryPartialObservationTests : IDisposable
{
    private static readonly DateTime Origin = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstScan = Origin.AddMinutes(1);
    private static readonly DateTime SecondScan = Origin.AddMinutes(10);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Origin));
    private readonly AppDbContext _db;
    private readonly PortRegistryService _sut;

    public PortRegistryPartialObservationTests()
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

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ObservedPortDto Observed(int port, string protocol) => new()
    {
        Port = port,
        Protocol = protocol,
        Holder = "daemon",
        Interface = "0.0.0.0"
    };

    private static ObservedProtocolScanDto Measured(string protocol, double ageSeconds = 0) =>
        new() { Protocol = protocol, AgeSeconds = ageSeconds };

    private async Task<List<(int Port, string Protocol)>> ObservedRowsAsync() =>
        (await _db.ServerPortReservations.AsNoTracking()
            .Where(row => row.Source == PortReservationSource.Observed)
            .ToListAsync(Ct))
        .Select(row => (row.Port, row.Protocol))
        .Order()
        .ToList();

    private async Task<DateTime?> ServerStampAsync() =>
        (await _db.Servers.AsNoTracking().SingleAsync(server => server.Id == 7, Ct)).PortsObservedAt;

    [Fact]
    public async Task AProtocolThatWasNotMeasured_KeepsItsObservations_WhileTheMeasuredOneIsReplaced()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(80, "tcp"), Observed(53, "udp")], FirstScan, Ct);

        // The UDP probe failed: the report names TCP only, and lists no UDP port.
        await _sut.ReplaceObservedAsync(
            7, [Observed(443, "tcp")], SecondScan, Ct, protocols: [Measured("tcp")]);

        Assert.Equal([(53, "udp"), (443, "tcp")], await ObservedRowsAsync());
    }

    [Fact]
    public async Task AReportThatMeasuredNothing_ChangesNothing()
    {
        await _sut.ReplaceObservedAsync(7, [Observed(80, "tcp"), Observed(53, "udp")], FirstScan, Ct);

        await _sut.ReplaceObservedAsync(7, [], SecondScan, Ct, protocols: []);

        Assert.Equal([(53, "udp"), (80, "tcp")], await ObservedRowsAsync());
        Assert.Equal(FirstScan, await ServerStampAsync());
    }

    [Fact]
    public async Task ACachedScan_IsDatedByItsRealMeasurement_NotByItsArrival()
    {
        var age = TimeSpan.FromMinutes(4);

        await _sut.ReplaceObservedAsync(
            7, [Observed(80, "tcp")], SecondScan, Ct, protocols: [Measured("tcp", age.TotalSeconds)]);

        var measuredAt = SecondScan - age;
        var row = await _db.ServerPortReservations.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(measuredAt, row.ObservedAt);
        Assert.Equal(measuredAt, await ServerStampAsync());
    }

    [Fact]
    public async Task WithoutATcpMeasurement_TheServerStampAndTheClaimsKeepTheirLastConfirmedTime()
    {
        await _sut.DeclareAsync(7, [10031], "project:4", "portfolio-prod-front", 4, Ct);
        await _sut.ReplaceObservedAsync(7, [Observed(10031, "tcp")], FirstScan, Ct);

        // Only UDP was measured: nothing was learned about the TCP claims, so their "listening now"
        // reference (the server stamp) must not move past their own stamp.
        await _sut.ReplaceObservedAsync(
            7, [Observed(53, "udp")], SecondScan, Ct, protocols: [Measured("udp")]);

        var claim = await _db.ServerPortReservations.AsNoTracking()
            .SingleAsync(row => row.Source == PortReservationSource.Declared, Ct);
        Assert.Equal(FirstScan, claim.ObservedAt);
        Assert.Equal(FirstScan, await ServerStampAsync());
        Assert.Equal([(53, "udp"), (10031, "tcp")], await ObservedRowsAsync());
    }

    [Fact]
    public async Task AReportForAServerThatIsGone_WritesNothingEvenWhenOnlyUdpWasMeasured()
    {
        await _sut.ReplaceObservedAsync(
            99, [Observed(53, "udp")], SecondScan, Ct, protocols: [Measured("udp")]);

        Assert.Empty(await _db.ServerPortReservations.AsNoTracking().ToListAsync(Ct));
    }
}
