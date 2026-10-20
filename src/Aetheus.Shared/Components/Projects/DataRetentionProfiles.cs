// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>Defines profile defaults and override bounds; explicit project overrides take
/// precedence.</summary>
public static class DataRetentionProfiles
{
    public const int MinimumExportIntervalMinutes = 1;
    public const int MaximumExportIntervalMinutes = 60;
    public const int MinimumTelemetryDetailDays = 1;
    public const int MaximumTelemetryDetailDays = 90;
    public const int MinimumAggregateMonths = 1;
    public const int MaximumAggregateMonths = 36;
    public const int MinimumAnalysisDetailDays = 1;
    public const int MaximumAnalysisDetailDays = 365;
    public const int MinimumAppLogDays = 1;
    public const int MaximumAppLogDays = 90;

    public static readonly DataRetentionValues Light = new(15, 2, 3, 7, 7);
    public static readonly DataRetentionValues Standard = new(5, 7, 13, 30, 14);
    public static readonly DataRetentionValues Complete = new(1, 30, 13, 90, 30);

    /// <summary>The profiles in the order they are offered, from the least data to the most.</summary>
    public static readonly IReadOnlyList<DataRetentionProfile> Ordered =
        [DataRetentionProfile.Light, DataRetentionProfile.Standard, DataRetentionProfile.Complete];

    public static DataRetentionValues Of(DataRetentionProfile profile) => profile switch
    {
        DataRetentionProfile.Light => Light,
        DataRetentionProfile.Complete => Complete,
        _ => Standard
    };

    /// <summary>The values a project applies: each override within its bounds, or the profile's value.</summary>
    public static DataRetentionValues Resolve(DataRetentionProfile profile, DataRetentionOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var defaults = Of(profile);
        return new DataRetentionValues(
            Pick(overrides.TelemetryExportIntervalMinutes, defaults.TelemetryExportIntervalMinutes,
                MinimumExportIntervalMinutes, MaximumExportIntervalMinutes),
            Pick(overrides.TelemetryDetailRetentionDays, defaults.TelemetryDetailRetentionDays,
                MinimumTelemetryDetailDays, MaximumTelemetryDetailDays),
            Pick(overrides.TelemetryAggregateRetentionMonths, defaults.TelemetryAggregateRetentionMonths,
                MinimumAggregateMonths, MaximumAggregateMonths),
            Pick(overrides.AnalysisDetailRetentionDays, defaults.AnalysisDetailRetentionDays,
                MinimumAnalysisDetailDays, MaximumAnalysisDetailDays),
            Pick(overrides.AppLogRetentionDays, defaults.AppLogRetentionDays,
                MinimumAppLogDays, MaximumAppLogDays));
    }

    private static int Pick(int? value, int fallback, int minimum, int maximum) =>
        value.HasValue ? Math.Clamp(value.Value, minimum, maximum) : fallback;
}
