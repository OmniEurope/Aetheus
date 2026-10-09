// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>
/// PLAN-013 lot 4: the five values of a project's data retention, as applied. The telemetry send interval
/// is handed to the <c>Aetheus.Telemetry</c> package of each application of the project; the four
/// durations are read by the retention sweeps.
/// </summary>
public sealed record DataRetentionValues(
    int TelemetryExportIntervalMinutes,
    int TelemetryDetailRetentionDays,
    int TelemetryAggregateRetentionMonths,
    int AnalysisDetailRetentionDays,
    int AppLogRetentionDays);
