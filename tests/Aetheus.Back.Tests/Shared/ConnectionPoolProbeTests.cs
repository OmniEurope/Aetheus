// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Aetheus.Back.Middleware;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aetheus.Back.Tests;

/// <summary>
/// R1-40: the pool figures come from the metrics Npgsql publishes. The probe is exercised on a meter of
/// its own with the same instrument names, and one test checks those names against the real library, so
/// a package update that renames an instrument fails here instead of silently reporting zeros.
/// </summary>
public sealed class ConnectionPoolProbeTests : IDisposable
{
    private readonly string _meterName = "npgsql-probe-tests-" + Guid.NewGuid().ToString("N");
    private readonly Meter _meter;
    private int _used;
    private int _idle;

    public ConnectionPoolProbeTests()
    {
        _meter = new Meter(_meterName);
    }

    public void Dispose() => _meter.Dispose();

    private Histogram<double> CreateTime() => _meter.CreateHistogram<double>(ConnectionPoolProbe.CreateTimeInstrument, "s");

    private Counter<long> Timeouts() => _meter.CreateCounter<long>(ConnectionPoolProbe.TimeoutsInstrument);

    private UpDownCounter<long> PendingRequests() => _meter.CreateUpDownCounter<long>(ConnectionPoolProbe.PendingRequestsInstrument);

    private void ConnectionCounts()
    {
        _meter.CreateObservableUpDownCounter(ConnectionPoolProbe.ConnectionCountInstrument, () => new[]
        {
            new Measurement<int>(_used, new KeyValuePair<string, object?>("db.client.connection.state", "used")),
            new Measurement<int>(_idle, new KeyValuePair<string, object?>("db.client.connection.state", "idle"))
        });
    }

    [Fact]
    public void ConnectionCreations_AreCountedWithTheirTime_AndTimeoutsAreSummed()
    {
        var created = CreateTime();
        var timeouts = Timeouts();
        using var probe = new ConnectionPoolProbe(_meterName);

        created.Record(0.25);
        created.Record(0.5);
        timeouts.Add(3);

        var totals = probe.Totals();
        Assert.Equal(2, totals.Created);
        Assert.Equal(TimeSpan.FromMilliseconds(750), totals.CreateTime);
        Assert.Equal(3, totals.Timeouts);
    }

    [Fact]
    public void TheDifferenceOfTwoReadings_IsWhatHappenedBetweenThem()
    {
        var created = CreateTime();
        var timeouts = Timeouts();
        using var probe = new ConnectionPoolProbe(_meterName);
        created.Record(1.0);
        timeouts.Add(1);
        var before = probe.Totals();

        created.Record(2.0);
        timeouts.Add(4);
        var delta = probe.Totals().Since(before);

        Assert.Equal(1, delta.Created);
        Assert.Equal(TimeSpan.FromSeconds(2), delta.CreateTime);
        Assert.Equal(4, delta.Timeouts);
    }

    [Fact]
    public void TheLevels_AreReadOnDemand_AndFollowThePool()
    {
        var pending = PendingRequests();
        ConnectionCounts();
        using var probe = new ConnectionPoolProbe(_meterName);
        pending.Add(5);
        _used = 10;
        _idle = 2;

        var first = probe.Levels();
        pending.Add(-5);
        pending.Add(1);
        _used = 4;
        _idle = 8;
        var second = probe.Levels();

        Assert.Equal(new ConnectionPoolProbe.PoolLevels(5, 10, 2), first);
        // Waiting requests follow the up/down counter; in use and idle are read again, not added up.
        Assert.Equal(new ConnectionPoolProbe.PoolLevels(1, 4, 8), second);
    }

    // R-6: a creation at or above the threshold is reported on its own, with its time and the pool it belongs to;
    // a fast one writes nothing.
    [Fact]
    public void ASlowConnectionCreation_IsReportedOnItsOwn_AndAFastOneIsNot()
    {
        var created = CreateTime();
        var log = new RecordingLogger<ConnectionPoolProbe>();
        using var probe = new ConnectionPoolProbe(_meterName, TimeSpan.FromMilliseconds(500), log);

        created.Record(0.02);
        created.Record(2.04, new KeyValuePair<string, object?>("db.client.connection.pool.name", "Host=db;Database=aetheus"));

        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("2040 ms", warning.Message, StringComparison.Ordinal);
        Assert.Contains("threshold 500 ms", warning.Message, StringComparison.Ordinal);
        Assert.Contains("pool Host=db;Database=aetheus", warning.Message, StringComparison.Ordinal);
        Assert.Equal(2, probe.Totals().Created);
    }

    [Fact]
    public void WithoutAThreshold_NoCreationIsReported()
    {
        var created = CreateTime();
        var log = new RecordingLogger<ConnectionPoolProbe>();
        using var probe = new ConnectionPoolProbe(_meterName, TimeSpan.Zero, log);

        created.Record(9.0);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void AnotherMeter_IsIgnored()
    {
        using var other = new Meter("some-other-meter");
        var foreign = other.CreateHistogram<double>(ConnectionPoolProbe.CreateTimeInstrument, "s");
        using var probe = new ConnectionPoolProbe(_meterName);

        foreign.Record(9.0);

        Assert.Equal(0, probe.Totals().Created);
    }

    [Fact]
    public void WithoutAnyPool_EverythingReadsZero()
    {
        using var probe = new ConnectionPoolProbe(_meterName);

        Assert.Equal(default, probe.Totals());
        Assert.Equal(default, probe.Levels());
    }

    /// <summary>
    /// The instrument names and kinds this probe relies on exist in the Npgsql the application ships: the
    /// events (creation time, timeouts) and the waiting-requests level are pushed, the connection count is
    /// observable, which is what <see cref="ConnectionPoolProbe.Levels"/> relies on for each.
    /// </summary>
    [Fact]
    public void TheRealNpgsqlLibrary_PublishesTheInstrumentsTheProbeReads()
    {
        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == ConnectionPoolProbe.NpgsqlMeterName)
                found[instrument.Name] = instrument.IsObservable;
        };
        listener.Start();

        // Creating a data source is enough to publish the instruments; it opens no connection.
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none");

        Assert.False(found[ConnectionPoolProbe.CreateTimeInstrument]);
        Assert.False(found[ConnectionPoolProbe.TimeoutsInstrument]);
        Assert.False(found[ConnectionPoolProbe.PendingRequestsInstrument]);
        Assert.True(found[ConnectionPoolProbe.ConnectionCountInstrument]);
    }
}
