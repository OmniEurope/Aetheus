// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Telemetry;

/// <summary> How often the metrics are sent. It starts at the OpenTelemetry default (60 s, or the
/// <c>OTEL_METRIC_EXPORT_INTERVAL</c> variable in milliseconds), then takes the interval of the
/// application's project as soon as Aetheus hands it over (<see
/// cref="ServerConfigurationClient"/>). A server that does not hand it over (an older Aetheus)
/// leaves the starting interval in place. </summary>
internal sealed class MetricExportSchedule
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(1);
    private const string IntervalEnvironmentVariable = "OTEL_METRIC_EXPORT_INTERVAL";

    private long _intervalTicks;

    internal MetricExportSchedule(TimeSpan initial) => _intervalTicks = Clamp(initial).Ticks;

    internal TimeSpan Interval => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));

    /// <summary>The interval the host set through <c>OTEL_METRIC_EXPORT_INTERVAL</c>, or 60 s.</summary>
    internal static MetricExportSchedule FromEnvironment() =>
        new(long.TryParse(
                Environment.GetEnvironmentVariable(IntervalEnvironmentVariable),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : DefaultInterval);

    /// <summary>Applies the interval Aetheus sent, kept between 10 s and 1 h.</summary>
    internal void Apply(int seconds) =>
        Interlocked.Exchange(ref _intervalTicks, Clamp(TimeSpan.FromSeconds(seconds)).Ticks);

    private static TimeSpan Clamp(TimeSpan value) =>
        value < MinimumInterval ? MinimumInterval : value > MaximumInterval ? MaximumInterval : value;
}
