// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Alerts;

/// <summary>
/// The unit an alert threshold is compared in, as <c>AlertEvaluatorService.ReadMetricValue</c> reads the
/// metric: CPU, memory and disk usage are percentages, free disk space and build cache size are GiB
/// (SITE-FUNC-ALERT-UNIT-001: a "less than 20 GiB free" rule read as "less than 20%").
/// </summary>
internal static class AlertThresholdUnit
{
    public static string Of(MetricType metric) => metric switch
    {
        MetricType.DiskFree or MetricType.BuildCache => "GiB",
        _ => "%"
    };

    /// <summary>The threshold with its unit: "20%", "20 GiB".</summary>
    public static string Format(MetricType metric, double threshold)
    {
        var unit = Of(metric);
        return unit == "%" ? $"{threshold}%" : $"{threshold} {unit}";
    }
}
