// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Middleware;

/// <summary>
/// Reads the connection pool figures Npgsql publishes as metrics (the "Npgsql" meter), without a
/// round-trip to the database: how many connections were created and how long that took, how many pool
/// timeouts happened, how many requests wait for a connection right now and, on demand, how many
/// connections are in use or idle. Npgsql publishes no "time spent waiting for a connection" histogram in this
/// version, so a pause caused by pool exhaustion shows as pending requests, a timeout or slow creations,
/// not as a measured wait (recette R1-40). Pool figures are process-wide, summed over every pool.
/// </summary>
internal sealed class ConnectionPoolProbe : IDisposable
{
    internal const string NpgsqlMeterName = "Npgsql";
    internal const string CreateTimeInstrument = "db.client.connection.npgsql.create_time";
    internal const string TimeoutsInstrument = "db.client.connection.npgsql.timeouts";
    internal const string PendingRequestsInstrument = "db.client.connection.npgsql.pending_requests";
    internal const string ConnectionCountInstrument = "db.client.connection.count";
    private const string StateTag = "db.client.connection.state";

    private readonly MeterListener _listener = new();
    private readonly Lock _observeGate = new();
    private long _created;
    private long _createTicks;
    private long _timeouts;
    private long _pending;
    private int _used;
    private int _idle;

    private readonly ILogger? _logger;
    private readonly double _slowCreationSeconds;

    public ConnectionPoolProbe() : this(NpgsqlMeterName)
    {
    }

    /// <summary>The probe the application runs: it also writes one Warning for each connection creation that
    /// takes at least <see cref="SlowRequestDiagnosticsOptions.SlowConnectionCreationThreshold"/>.</summary>
    public ConnectionPoolProbe(IOptions<SlowRequestDiagnosticsOptions> options, ILogger<ConnectionPoolProbe> logger)
        : this(NpgsqlMeterName, options.Value.SlowConnectionCreationThreshold, logger)
    {
    }

    /// <param name="meterName">The meter to listen to; only a test passes another than Npgsql's.</param>
    internal ConnectionPoolProbe(string meterName, TimeSpan slowCreationThreshold = default, ILogger? logger = null)
    {
        _logger = logger;
        _slowCreationSeconds = slowCreationThreshold.TotalSeconds;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name is CreateTimeInstrument or TimeoutsInstrument or PendingRequestsInstrument or ConnectionCountInstrument)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<int>(
            (instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.Start();
    }

    /// <summary>Connections created, the time they took and pool timeouts, since the process started.</summary>
    public PoolTotals Totals() => new(
        Interlocked.Read(ref _created),
        TimeSpan.FromTicks(Interlocked.Read(ref _createTicks)),
        Interlocked.Read(ref _timeouts));

    /// <summary>
    /// The figures that are levels, not events. Waiting requests are the running sum of Npgsql's up/down
    /// counter since this probe started (it starts with the application, before any pool exists); the
    /// connections in use and idle are asked of Npgsql now.
    /// </summary>
    public PoolLevels Levels()
    {
        lock (_observeGate)
        {
            _used = 0;
            _idle = 0;
            _listener.RecordObservableInstruments();
            return new PoolLevels((int)Math.Max(0, Interlocked.Read(ref _pending)), _used, _idle);
        }
    }

    public void Dispose() => _listener.Dispose();

    private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        switch (instrument)
        {
            case CreateTimeInstrument:
                Interlocked.Increment(ref _created);
                Interlocked.Add(ref _createTicks, (long)(value * TimeSpan.TicksPerSecond));
                // One creation on its own: the sum in a slow request's line hides whether four creations took
                // 600 ms each or one took 2.4 s, and says nothing when no request lasted long enough to be reported.
                if (_logger is not null && _slowCreationSeconds > 0 && value >= _slowCreationSeconds)
                    _logger.LogWarning(
                        "Slow Npgsql connection creation: {ElapsedMs} ms (threshold {ThresholdMs} ms), pool {Pool}",
                        (long)(value * 1000),
                        (long)(_slowCreationSeconds * 1000),
                        PoolName(tags));
                break;
            case TimeoutsInstrument:
                Interlocked.Add(ref _timeouts, (long)value);
                break;
            case PendingRequestsInstrument:
                Interlocked.Add(ref _pending, (long)value);
                break;
            case ConnectionCountInstrument:
                foreach (var tag in tags)
                {
                    if (tag.Key != StateTag) continue;
                    if (Equals(tag.Value, "used")) _used += (int)value;
                    else if (Equals(tag.Value, "idle")) _idle += (int)value;
                }
                break;
        }
    }

    // The pool's name as Npgsql tags it (host, port, database), or "unknown" when the tag is absent.
    private static string PoolName(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
            if (tag.Key == "db.client.connection.pool.name")
                return tag.Value?.ToString() ?? "unknown";
        return "unknown";
    }

    internal readonly record struct PoolTotals(long Created, TimeSpan CreateTime, long Timeouts)
    {
        public PoolTotals Since(PoolTotals earlier) => new(Created - earlier.Created, CreateTime - earlier.CreateTime, Timeouts - earlier.Timeouts);
    }

    internal readonly record struct PoolLevels(int PendingRequests, int InUse, int Idle);
}
