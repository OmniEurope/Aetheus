// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;

namespace Aetheus.Telemetry.Tests;

/// <summary>Verifies catalog size and statistics measurements, unknown row estimates and one export
/// per named table. Real PostgreSQL reads are covered by <c>DatabaseSizeQueryIntegrationTests</c>
/// in the backend integration suite.</summary>
public sealed class DatabaseSizeMetricsTests
{
    [Fact]
    public void TheStatements_ReadOnlyTheCatalog_AndBoundTheTables()
    {
        Assert.Equal("SELECT current_database(), pg_database_size(current_database())", DatabaseSizeQuery.DatabaseStatement);
        var tables = DatabaseSizeQuery.TablesStatement;
        // Pinned word for word (AGENTS.md, "No raw SQL"): a change of the statement is a reviewed change.
        Assert.Equal(
            "SELECT n.nspname, c.relname, pg_total_relation_size(c.oid), c.reltuples::float8 "
            + "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
            + "WHERE c.relkind IN ('r', 'm') "
            + "AND n.nspname NOT IN ('pg_catalog', 'information_schema') "
            + "AND n.nspname NOT LIKE 'pg\\_toast%' AND n.nspname NOT LIKE 'pg\\_temp%' "
            + "ORDER BY pg_total_relation_size(c.oid) DESC, n.nspname, c.relname "
            + "LIMIT @top",
            tables);
        Assert.Contains("FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace ", tables, StringComparison.Ordinal);
        Assert.Contains("pg_total_relation_size(c.oid)", tables, StringComparison.Ordinal);
        Assert.Contains("c.reltuples::float8", tables, StringComparison.Ordinal);
        Assert.Contains("c.relkind IN ('r', 'm')", tables, StringComparison.Ordinal);
        Assert.EndsWith("LIMIT @" + DatabaseSizeQuery.TopParameter, tables, StringComparison.Ordinal);
        // One FROM, one JOIN, both catalog relations: no application table is ever selected.
        Assert.Equal(1, Count(tables, " FROM "));
        Assert.Equal(1, Count(tables, " JOIN "));
        Assert.DoesNotContain("*", tables, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1d, null)]
    [InlineData(0d, 0L)]
    [InlineData(1234.6d, 1235L)]
    [InlineData(8_418_471d, 8_418_471L)]
    public void TheRowEstimate_IsUnknownWhenNeverAnalysed(double reltuples, long? expected)
    {
        var table = DatabaseSizeQuery.MapTable("public", "AnalysisMetrics", 2_542_796_800, reltuples);

        Assert.Equal(new DatabaseTableSize("public", "AnalysisMetrics", 2_542_796_800, expected), table);
    }

    [Fact]
    public void ASize_IsNeverNegative() =>
        Assert.Equal(0, DatabaseSizeQuery.MapTable("public", "T", -5, 0).TotalBytes);

    [Fact]
    public void AMeasurement_IsExportedOnce_WithItsDatabaseAndTablesNamed()
    {
        using var metrics = new DatabaseSizeMetrics(new DatabaseSizeOptions(), TimeProvider.System);
        using var listener = Listen(out var recorded);

        listener.RecordObservableInstruments();
        Assert.Empty(recorded); // nothing measured yet: nothing exported, not a zero

        metrics.Publish(new DatabaseSizeSnapshot("aetheus", 6_595_543_040,
        [
            new DatabaseTableSize("public", "AppMetricSamples", 3_197_108_224, 6_631_915),
            new DatabaseTableSize("audit", "Fresh", 8192, null)
        ]));
        listener.RecordObservableInstruments();

        var database = Assert.Single(recorded, item => item.Name == DatabaseSizeMetrics.DatabaseSizeMetric);
        Assert.Equal(6_595_543_040, database.Value);
        Assert.Equal("aetheus", database.Tags[DatabaseSizeMetrics.DatabaseAttribute]);
        var sizes = recorded.Where(item => item.Name == DatabaseSizeMetrics.TableSizeMetric).ToList();
        Assert.Equal(2, sizes.Count);
        Assert.Contains(sizes, item => item.Value == 3_197_108_224
            && item.Tags[DatabaseSizeMetrics.TableAttribute] == "AppMetricSamples"
            && item.Tags[DatabaseSizeMetrics.SchemaAttribute] == "public");
        // A table PostgreSQL never analysed has a size but no row estimate.
        var rows = Assert.Single(recorded, item => item.Name == DatabaseSizeMetrics.TableRowsMetric);
        Assert.Equal(6_631_915, rows.Value);
        Assert.Equal("AppMetricSamples", rows.Tags[DatabaseSizeMetrics.TableAttribute]);

        recorded.Clear();
        listener.RecordObservableInstruments();
        Assert.Empty(recorded); // the same measurement is not exported a second time

        metrics.Publish(new DatabaseSizeSnapshot("aetheus", 1, []));
        listener.RecordObservableInstruments();
        Assert.Equal(1, Assert.Single(recorded).Value);
    }

    [Fact]
    public async Task WithoutAConnection_AMeasurementFailsHonestly()
    {
        using var metrics = new DatabaseSizeMetrics(new DatabaseSizeOptions(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => metrics.MeasureAsync(TestContext.Current.CancellationToken));
    }

    private static int Count(string text, string part) =>
        (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;

    private static MeterListener Listen(out List<RecordedMeasurement> recorded)
    {
        var sink = new List<RecordedMeasurement>();
        recorded = sink;
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
            sink.Add(new RecordedMeasurement(instrument.Name, value, named));
        });
        listener.Start();
        return listener;
    }

    private sealed record RecordedMeasurement(string Name, long Value, Dictionary<string, string?> Tags);
}
