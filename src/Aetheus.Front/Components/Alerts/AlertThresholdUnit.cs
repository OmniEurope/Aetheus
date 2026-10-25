// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Alerts;

/// <summary>
/// The unit an alert threshold is compared in, as <c>AlertEvaluatorService.ReadMetricValue</c> reads the
/// metric: CPU, memory and disk usage are percentages, free disk space and build cache size are Go,
/// the clock offset is seconds (SITE-FUNC-ALERT-UNIT-001: a "less than 20 Go free" rule read as
/// "less than 20%").
/// </summary>
internal static class AlertThresholdUnit
{
    public static string Of(MetricType metric) => metric switch
    {
        MetricType.DiskFree or MetricType.BuildCache => "Go",
        MetricType.ClockOffset => "s",
        _ => "%"
    };

    /// <summary>The threshold with its unit: "20%", "20 Go".</summary>
    public static string Format(MetricType metric, double threshold)
    {
        var unit = Of(metric);
        return unit == "%" ? $"{threshold}%" : $"{threshold} {unit}";
    }
}
