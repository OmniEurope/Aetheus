// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Projects;

/// <summary>The values a project sets itself; null keeps the profile's value.</summary>
public sealed record DataRetentionOverrides
{
    public static readonly DataRetentionOverrides None = new();

    [Range(DataRetentionProfiles.MinimumExportIntervalMinutes, DataRetentionProfiles.MaximumExportIntervalMinutes)]
    public int? TelemetryExportIntervalMinutes { get; init; }

    [Range(DataRetentionProfiles.MinimumTelemetryDetailDays, DataRetentionProfiles.MaximumTelemetryDetailDays)]
    public int? TelemetryDetailRetentionDays { get; init; }

    [Range(DataRetentionProfiles.MinimumAggregateMonths, DataRetentionProfiles.MaximumAggregateMonths)]
    public int? TelemetryAggregateRetentionMonths { get; init; }

    [Range(DataRetentionProfiles.MinimumAnalysisDetailDays, DataRetentionProfiles.MaximumAnalysisDetailDays)]
    public int? AnalysisDetailRetentionDays { get; init; }

    [Range(DataRetentionProfiles.MinimumAppLogDays, DataRetentionProfiles.MaximumAppLogDays)]
    public int? AppLogRetentionDays { get; init; }
}
