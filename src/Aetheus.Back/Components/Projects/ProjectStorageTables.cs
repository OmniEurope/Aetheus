// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>Defines storage tables and fallback bytes-per-row estimates when database size
/// measurements omit a table. Fallbacks approximate measured size per estimated row; the log value
/// is a rounded estimate.</summary>
internal static class ProjectStorageTables
{
    public const string MetricSamples = "AppMetricSamples";
    public const string MetricHourly = "AppMetricHourly";
    public const string Logs = "AppLogEntries";
    public const string AnalysisMetrics = "AnalysisMetrics";
    public const string AnalysisComponents = "AnalysisComponents";

    /// <summary>How far a measured bytes per row may stray from the fallback, either way.</summary>
    public const double MeasuredBound = 2;

    public static readonly IReadOnlyDictionary<string, double> FallbackBytesPerRow = new Dictionary<string, double>
    {
        [MetricSamples] = 480,
        [MetricHourly] = 300,
        [Logs] = 1_000,
        [AnalysisMetrics] = 300,
        [AnalysisComponents] = 220
    };

    /// <summary>
    /// The bytes per row of each table: measured (size over estimated rows) when the measurement has both,
    /// the fallback otherwise. A measured value is kept between half and twice the fallback: a purge
    /// deletes rows but the table keeps its size on disk until « Récupérer la place », so size over the
    /// remaining rows grows with every purge. Uncapped, a project alone in a table would keep the whole
    /// table size as its usage and the budget would purge it to nothing.
    /// </summary>
    public static Dictionary<string, double> BytesPerRow(AppDatabaseSizeReportDto? measurement)
    {
        var bytes = new Dictionary<string, double>(FallbackBytesPerRow, StringComparer.Ordinal);
        foreach (var table in measurement?.Tables ?? [])
            if (bytes.ContainsKey(table.Name) && table.TotalBytes is > 0 && table.EstimatedRows is > 0)
                bytes[table.Name] = Math.Clamp(
                    (double)table.TotalBytes.Value / table.EstimatedRows.Value,
                    FallbackBytesPerRow[table.Name] / MeasuredBound,
                    FallbackBytesPerRow[table.Name] * MeasuredBound);
        return bytes;
    }
}
