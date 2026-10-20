// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Shared;

/// <summary>Provide localized source context and export values for telemetry rows so exported logs,
/// errors and route timings remain interpretable outside the page.</summary>
public static class AppTelemetryMarkdownExport
{
    /// <summary>The header lines of a Logs export: the application and the period the grid reads.</summary>
    public static IReadOnlyList<OmniTableExportField> LogsFields(int appId, string? appName, int hours, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            new(text("Application"), Application(appName, appId)),
            new(text("Period"), Format(text("LogsExportPeriodValue"), hours.ToString("N0", CultureInfo.CurrentCulture)))
        ];
    }

    /// <summary>The header lines of an Errors export: the application and the groups it holds.</summary>
    public static IReadOnlyList<OmniTableExportField> ErrorsFields(int appId, string? appName, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            new(text("Application"), Application(appName, appId)),
            new(text("Period"), text("ErrorsExportPeriodValue"))
        ];
    }

    /// <summary>The header lines of a Performance export, everything the tab shows above the table
    /// (when the figures were sent and since when they count, the three figures, how the
    /// percentiles are taken), timestamps in UTC ISO 8601.</summary>
    public static IReadOnlyList<OmniTableExportField> PerformanceFields(
        int appId, string? appName, AppPerformanceReportDto report, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(text);
        var none = text("ExportNone");
        var routes = report.Routes;
        var slowest = routes.Where(route => route.P95Ms is not null).MaxBy(route => route.P95Ms);
        return
        [
            new(text("Application"), Application(appName, appId)),
            new(text("AppPerformanceExportMeasuredAt"), report.MeasuredAt is { } measuredAt ? IsoUtc(measuredAt) : none),
            new(text("AppPerformanceExportWindowSince"), report.WindowSince is { } since ? IsoUtc(since) : none),
            new(text("AppPerformanceRoutes"), routes.Count.ToString(CultureInfo.InvariantCulture)),
            new(text("AppPerformanceRequests"), routes.Sum(route => route.Count ?? 0).ToString(CultureInfo.InvariantCulture)),
            new(text("AppPerformanceSlowestRoute"), slowest is null
                ? none
                : string.Create(CultureInfo.InvariantCulture, $"{slowest.Method} {slowest.Route} ({slowest.P95Ms:0.###} ms)")),
            new(text("AppPerformanceExportDurations"), text("AppPerformanceExportDurationsValue")),
            new(text("AppPerformanceExportPercentiles"), text("AppPerformanceExportPercentilesValue"))
        ];
    }

    /// <summary>The title of an export: the format's text with the application.</summary>
    public static string Title(string format, int appId, string? appName) => Format(format, Application(appName, appId));

    /// <summary>A log line's severity as the Severity column writes it: its label, then its OTLP number.</summary>
    public static string SeverityExport(AppLogEntryDto entry, Func<string, string> text) =>
        string.Create(CultureInfo.InvariantCulture, $"{SeverityLabel(entry, text)} ({entry.SeverityNumber})");

    /// <summary>An error group's message as the Message column writes it: the message, then the top
    /// frame the cell shows under it, when there is one.</summary>
    public static string ErrorMessageExport(AppErrorEventDto error) => string.IsNullOrEmpty(error.TopFrame)
        ? error.Message
        : string.Concat(error.Message, " (", error.TopFrame, ")");

    /// <summary>A duration in milliseconds as an export writes it: rounded to the microsecond, still a number.</summary>
    public static double? MillisecondsExport(double? value) => value is { } milliseconds ? Math.Round(milliseconds, 3) : null;

    /// <summary>
    /// The OTLP severity class of a severity number (four numbers per class, 1 to 24), the names the
    /// Severity column filters on; null for an unspecified severity (0).
    /// </summary>
    public static string? SeverityClass(int severityNumber) => severityNumber switch
    {
        >= 21 => "FATAL",
        >= 17 => "ERROR",
        >= 13 => "WARN",
        >= 9 => "INFO",
        >= 5 => "DEBUG",
        >= 1 => "TRACE",
        _ => null
    };

    /// <summary> The localized name of an OTLP severity class ("Avertissement" for WARN), shown by
    /// the badge of a line and by the Severity filter alike. An unknown class keeps its own name.
    /// </summary>
    public static string SeverityClassLabel(string severityClass, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return severityClass switch
        {
            "TRACE" => text("LogSeverityTrace"),
            "DEBUG" => text("LogSeverityDebug"),
            "INFO" => text("LogSeverityInfo"),
            "WARN" => text("LogSeverityWarn"),
            "ERROR" => text("LogSeverityError"),
            "FATAL" => text("LogSeverityFatal"),
            _ => severityClass
        };
    }

    /// <summary> The label a log line's severity shows, the localized name of the class of its
    /// severity number, never the raw OTLP <c>SeverityText</c> (each exporter spells it its own
    /// way: "Warning", "WARN", "warn"). That text only names a line whose number is unspecified.
    /// </summary>
    public static string SeverityLabel(AppLogEntryDto entry, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return SeverityClass(entry.SeverityNumber) is { } severityClass
            ? SeverityClassLabel(severityClass, text)
            : string.IsNullOrEmpty(entry.SeverityText) ? "-" : entry.SeverityText;
    }

    /// <summary>The front reads server timestamps as local time (<c>UtcToLocalDateTimeConverter</c>);
    /// the export writes them back in UTC, ISO 8601, to the millisecond.</summary>
    public static string IsoUtc(DateTime timestamp)
    {
        var utc = timestamp.Kind switch
        {
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
            _ => timestamp
        };
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static string Format(string format, string argument) => string.Format(CultureInfo.CurrentCulture, format, argument);

    private static string Application(string? appName, int appId) => string.IsNullOrWhiteSpace(appName)
        ? string.Create(CultureInfo.InvariantCulture, $"#{appId}")
        : string.Create(CultureInfo.InvariantCulture, $"{appName} (#{appId})");
}
