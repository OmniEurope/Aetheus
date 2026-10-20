// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Aetheus.Telemetry;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Compares hosted database-size gauges with PostgreSQL catalog measurements within the
/// specified tolerance, including row estimates.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseSizeMeasurementIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const int ProbeRows = 20_000;

    [Fact]
    public async Task TheMeasuredSizes_EqualThePsqlMeasures_WithinFivePercent()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(connection => new NpgsqlCommand(
            "CREATE TABLE size_probe (id integer PRIMARY KEY, payload text NOT NULL); "
            + "INSERT INTO size_probe SELECT g, repeat('x', 200) FROM generate_series(1, 20000) g; " // ProbeRows
            + "ANALYZE size_probe;", connection), ct);

        var recorded = new List<(string Name, long Value, Dictionary<string, string?> Tags)>();
        using var listener = Listen(recorded);
        var options = new DatabaseSizeOptions { TopTables = 100 }
            .UsePostgreSql(() => new NpgsqlConnection(ConnectionString));
        using var metrics = new DatabaseSizeMetrics(options, TimeProvider.System);
        await metrics.StartAsync(ct);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (recorded.Count == 0 && DateTime.UtcNow < deadline)
            {
                listener.RecordObservableInstruments();
                await Task.Delay(100, ct);
            }
        }
        finally
        {
            await metrics.StopAsync(CancellationToken.None);
        }

        var psqlDatabase = await ScalarAsync(connection => new NpgsqlCommand("SELECT pg_database_size(current_database())", connection), ct);
        var psqlProbe = await ScalarAsync(connection => new NpgsqlCommand("SELECT pg_total_relation_size('public.size_probe')", connection), ct);

        var database = Assert.Single(recorded, item => item.Name == DatabaseSizeMetrics.DatabaseSizeMetric);
        AssertWithinFivePercent(psqlDatabase, database.Value);
        Assert.Equal(new NpgsqlConnectionStringBuilder(ConnectionString).Database, database.Tags[DatabaseSizeMetrics.DatabaseAttribute]);

        var probe = Assert.Single(recorded, item => item.Name == DatabaseSizeMetrics.TableSizeMetric
            && item.Tags[DatabaseSizeMetrics.TableAttribute] == "size_probe");
        Assert.Equal("public", probe.Tags[DatabaseSizeMetrics.SchemaAttribute]);
        AssertWithinFivePercent(psqlProbe, probe.Value);
        Assert.True(probe.Value > ProbeRows * 200L, $"the probe table holds its rows: {probe.Value} bytes");
        var rows = Assert.Single(recorded, item => item.Name == DatabaseSizeMetrics.TableRowsMetric
            && item.Tags[DatabaseSizeMetrics.TableAttribute] == "size_probe");
        AssertWithinFivePercent(ProbeRows, rows.Value);

        // The tables come largest first, bounded by TopTables, the migrated schema's tables among them, never
        // the catalog.
        var tables = recorded.Where(item => item.Name == DatabaseSizeMetrics.TableSizeMetric).ToList();
        Assert.InRange(tables.Count, 2, 100);
        Assert.Equal(tables.Select(item => item.Value).OrderDescending(), tables.Select(item => item.Value));
        Assert.Contains(tables, item => item.Tags[DatabaseSizeMetrics.TableAttribute] != "size_probe");
        Assert.DoesNotContain(tables, item => item.Tags[DatabaseSizeMetrics.SchemaAttribute] is "pg_catalog" or "information_schema");
    }

    private static void AssertWithinFivePercent(long expected, long actual) =>
        Assert.True(Math.Abs(actual - expected) <= expected * 0.05,
            $"measured {actual}, psql {expected}: more than 5 percent apart");

    private async Task ExecuteAsync(Func<NpgsqlConnection, NpgsqlCommand> create, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = create(connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> ScalarAsync(Func<NpgsqlConnection, NpgsqlCommand> create, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = create(connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static MeterListener Listen(List<(string Name, long Value, Dictionary<string, string?> Tags)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == DatabaseSizeMetrics.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var named = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
                named[tag.Key] = tag.Value?.ToString();
            lock (sink)
                sink.Add((instrument.Name, value, named));
        });
        listener.Start();
        return listener;
    }
}
