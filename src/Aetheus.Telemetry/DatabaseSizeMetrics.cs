// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Telemetry;

/// <summary>
/// PLAN-013 lot 1 (R2-113): measures the host's database every <see cref="DatabaseSizeOptions.IntervalMinutes"/>
/// and publishes three gauges on the meter <see cref="MeterName"/>:
/// <list type="bullet">
/// <item><see cref="DatabaseSizeMetric"/>: the whole database, in bytes, attribute <see cref="DatabaseAttribute"/>;</item>
/// <item><see cref="TableSizeMetric"/>: each of the largest tables, in bytes (heap, indexes, TOAST);</item>
/// <item><see cref="TableRowsMetric"/>: the same tables' estimated row count (<c>pg_class.reltuples</c>),
/// absent while PostgreSQL has never analysed the table.</item>
/// </list>
/// A table series carries <see cref="SchemaAttribute"/> and <see cref="TableAttribute"/>. Nothing of a
/// table's content is read.
///
/// A measurement is exported once: the gauges report it at the first collection after it was taken, then
/// nothing until the next one, so a 15-minute interval writes one point per series every 15 minutes, not
/// one per export minute (the lesson of R2-082).
/// </summary>
public sealed class DatabaseSizeMetrics : BackgroundService
{
    public const string MeterName = "Aetheus.Telemetry.Database";
    public const string DatabaseSizeMetric = "aetheus.db.size";
    public const string TableSizeMetric = "aetheus.db.table.size";
    public const string TableRowsMetric = "aetheus.db.table.rows";
    public const string DatabaseAttribute = "db.namespace";
    public const string SchemaAttribute = "db.schema.name";
    public const string TableAttribute = "db.collection.name";

    private readonly DatabaseSizeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Meter _meter = new(MeterName);
    private readonly object _gate = new();
    private DatabaseSizeSnapshot? _snapshot;
    private long _version;
    private long _databaseExported;
    private long _tableSizeExported;
    private long _tableRowsExported;

    public DatabaseSizeMetrics(DatabaseSizeOptions options, TimeProvider time, ILogger<DatabaseSizeMetrics>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _meter.CreateObservableGauge(DatabaseSizeMetric, MeasureDatabase, "By",
            "Size of the database on disk.");
        _meter.CreateObservableGauge(TableSizeMetric, MeasureTableSizes, "By",
            "Size on disk of one of the largest tables, indexes and TOAST included.");
        _meter.CreateObservableGauge(TableRowsMetric, MeasureTableRows, "{row}",
            "Estimated row count of one of the largest tables (planner statistics).");
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(_options.IntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MeasureAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Mandatory: a database that cannot be measured now (restart, network, permission) must
                // not stop the host nor the next measurements. No point is exported for this round.
                _logger.LogWarning(exception, "Database size measurement failed; next attempt in {Interval}", interval);
            }
            await Task.Delay(interval, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Takes one measurement through the configured connection and publishes it.</summary>
    internal async Task MeasureAsync(CancellationToken ct)
    {
        var factory = _options.ConnectionFactory
                      ?? throw new InvalidOperationException("No connection factory is configured.");
        await using var connection = factory();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        Publish(await DatabaseSizeQuery.ReadAsync(connection, _options.TopTables, ct).ConfigureAwait(false));
    }

    /// <summary>Makes <paramref name="snapshot"/> the measurement the gauges report at their next collection.</summary>
    internal void Publish(DatabaseSizeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _snapshot = snapshot;
            _version++;
        }
    }

    internal IEnumerable<Measurement<long>> MeasureDatabase() =>
        TakeOnce(ref _databaseExported, snapshot =>
        [
            new Measurement<long>(snapshot.DatabaseBytes,
                new KeyValuePair<string, object?>(DatabaseAttribute, snapshot.DatabaseName))
        ]);

    internal IEnumerable<Measurement<long>> MeasureTableSizes() =>
        TakeOnce(ref _tableSizeExported, snapshot => snapshot.Tables
            .Select(table => new Measurement<long>(table.TotalBytes, TableTags(table))));

    internal IEnumerable<Measurement<long>> MeasureTableRows() =>
        TakeOnce(ref _tableRowsExported, snapshot => snapshot.Tables
            .Where(table => table.EstimatedRows is not null)
            .Select(table => new Measurement<long>(table.EstimatedRows!.Value, TableTags(table))));

    private static KeyValuePair<string, object?>[] TableTags(DatabaseTableSize table) =>
    [
        new(SchemaAttribute, table.Schema),
        new(TableAttribute, table.Name)
    ];

    /// <summary>The measurement, the first time one gauge is collected after it was taken; nothing otherwise.</summary>
    private List<Measurement<long>> TakeOnce(
        ref long exportedVersion, Func<DatabaseSizeSnapshot, IEnumerable<Measurement<long>>> measure)
    {
        lock (_gate)
        {
            if (_snapshot is null || exportedVersion == _version)
                return [];
            exportedVersion = _version;
            return measure(_snapshot).ToList();
        }
    }
}
