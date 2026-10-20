// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;
using Aetheus.Telemetry;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>Maps exported database-size gauges into the Database view independently of
/// persistence.</summary>
public static class AppDatabaseSize
{
    public static readonly IReadOnlyList<string> MetricNames =
    [
        DatabaseSizeMetrics.DatabaseSizeMetric,
        DatabaseSizeMetrics.TableSizeMetric,
        DatabaseSizeMetrics.TableRowsMetric
    ];

    /// <summary>One measurement is exported at one collection: its points sit within seconds of each other.
    /// A table that left the largest ones has an older point, outside this spread, and is not listed.</summary>
    public static readonly TimeSpan MeasurementSpread = TimeSpan.FromMinutes(1);

    /// <summary>How far back the trend looks, and how wide the window of the measurement it compares with
    /// (a 15-minute interval leaves several points in it; the newest wins).</summary>
    public static readonly TimeSpan TrendDistance = TimeSpan.FromHours(24);
    public static readonly TimeSpan TrendWindow = TimeSpan.FromHours(2);

    public static AppDatabaseSizeReportDto Build(
        IReadOnlyList<AppMetricSample> latest, IReadOnlyList<AppMetricSample> dayBefore)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(dayBefore);
        if (latest.Count == 0) return new AppDatabaseSizeReportDto();

        var total = latest.FirstOrDefault(sample => sample.MetricName == DatabaseSizeMetrics.DatabaseSizeMetric);
        var rows = ByTable(latest, DatabaseSizeMetrics.TableRowsMetric);
        var previousSizes = ByTable(dayBefore, DatabaseSizeMetrics.TableSizeMetric);
        return new AppDatabaseSizeReportDto
        {
            MeasuredAt = latest.Max(sample => sample.Timestamp),
            DatabaseName = total is null ? null : ReadAttribute(total.AttributesJson, DatabaseSizeMetrics.DatabaseAttribute),
            TotalBytes = ToLong(total?.Value),
            TotalBytesDayBefore = ToLong(dayBefore
                .FirstOrDefault(sample => sample.MetricName == DatabaseSizeMetrics.DatabaseSizeMetric)?.Value),
            Tables = [.. ByTable(latest, DatabaseSizeMetrics.TableSizeMetric)
                .Select(table => new AppDatabaseTableSizeDto
                {
                    Schema = table.Key.Schema,
                    Name = table.Key.Name,
                    TotalBytes = ToLong(table.Value),
                    EstimatedRows = rows.TryGetValue(table.Key, out var count) ? ToLong(count) : null,
                    TotalBytesDayBefore = previousSizes.TryGetValue(table.Key, out var previous) ? ToLong(previous) : null
                })
                .OrderByDescending(table => table.TotalBytes ?? -1)
                .ThenBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Name, StringComparer.Ordinal)]
        };
    }

    /// <summary>The value of each table's series of <paramref name="metricName"/>, one per table.</summary>
    private static Dictionary<(string Schema, string Name), double> ByTable(
        IEnumerable<AppMetricSample> samples, string metricName)
    {
        var values = new Dictionary<(string Schema, string Name), double>();
        foreach (var sample in samples.Where(sample => sample.MetricName == metricName))
            if (ReadTable(sample.AttributesJson) is { } table)
                values.TryAdd(table, sample.Value);
        return values;
    }

    private static long? ToLong(double? value) => value is { } number ? (long)Math.Round(number) : null;

    private static (string Schema, string Name)? ReadTable(string? attributesJson)
    {
        var attributes = ReadAttributes(attributesJson);
        return attributes is not null
               && attributes.TryGetValue(DatabaseSizeMetrics.TableAttribute, out var name)
               && !string.IsNullOrWhiteSpace(name)
            ? (attributes.GetValueOrDefault(DatabaseSizeMetrics.SchemaAttribute) ?? string.Empty, name)
            : null;
    }

    private static string? ReadAttribute(string? attributesJson, string key) =>
        ReadAttributes(attributesJson)?.GetValueOrDefault(key);

    private static Dictionary<string, string>? ReadAttributes(string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(attributesJson);
        }
        catch (JsonException)
        {
            return null; // mandatory: a malformed attribute set cannot name a table
        }
    }
}
