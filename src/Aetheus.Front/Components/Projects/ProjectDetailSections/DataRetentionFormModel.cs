// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

/// <summary>PLAN-013 lot 4: the « Conservation des données » form; an empty value keeps the profile's.</summary>
public sealed class DataRetentionFormModel
{
    public DataRetentionProfile Profile { get; set; }

    [Range(DataRetentionProfiles.MinimumExportIntervalMinutes, DataRetentionProfiles.MaximumExportIntervalMinutes)]
    public int? TelemetryExportIntervalMinutes { get; set; }

    [Range(DataRetentionProfiles.MinimumTelemetryDetailDays, DataRetentionProfiles.MaximumTelemetryDetailDays)]
    public int? TelemetryDetailRetentionDays { get; set; }

    [Range(DataRetentionProfiles.MinimumAggregateMonths, DataRetentionProfiles.MaximumAggregateMonths)]
    public int? TelemetryAggregateRetentionMonths { get; set; }

    [Range(DataRetentionProfiles.MinimumAnalysisDetailDays, DataRetentionProfiles.MaximumAnalysisDetailDays)]
    public int? AnalysisDetailRetentionDays { get; set; }

    [Range(DataRetentionProfiles.MinimumAppLogDays, DataRetentionProfiles.MaximumAppLogDays)]
    public int? AppLogRetentionDays { get; set; }

    [Range(ProjectStorageDefaults.MinimumBudgetMegabytes, ProjectStorageDefaults.MaximumBudgetMegabytes)]
    public int? StorageBudgetMegabytes { get; set; }

    public static DataRetentionFormModel From(ProjectDataRetentionDto dto) => new()
    {
        StorageBudgetMegabytes = dto.StorageBudgetMegabytes,
        Profile = dto.Profile,
        TelemetryExportIntervalMinutes = dto.Overrides.TelemetryExportIntervalMinutes,
        TelemetryDetailRetentionDays = dto.Overrides.TelemetryDetailRetentionDays,
        TelemetryAggregateRetentionMonths = dto.Overrides.TelemetryAggregateRetentionMonths,
        AnalysisDetailRetentionDays = dto.Overrides.AnalysisDetailRetentionDays,
        AppLogRetentionDays = dto.Overrides.AppLogRetentionDays
    };

    public UpdateProjectDataRetentionRequest ToRequest() => new()
    {
        StorageBudgetMegabytes = StorageBudgetMegabytes,
        Profile = Profile,
        Overrides = new DataRetentionOverrides
        {
            TelemetryExportIntervalMinutes = TelemetryExportIntervalMinutes,
            TelemetryDetailRetentionDays = TelemetryDetailRetentionDays,
            TelemetryAggregateRetentionMonths = TelemetryAggregateRetentionMonths,
            AnalysisDetailRetentionDays = AnalysisDetailRetentionDays,
            AppLogRetentionDays = AppLogRetentionDays
        }
    };
}
