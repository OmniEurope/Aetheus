// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary>Shared validation, ingestion, probing, and retention limits for monitored applications.</summary>
public static class AppMonitoringDefaults
{
    public const int MinimumProbeIntervalSeconds = 15;
    public const int DefaultProbeIntervalSeconds = 60;
    public const int MaximumProbeIntervalSeconds = 3600;
    public const int MinimumProbeTimeoutSeconds = 1;
    public const int DefaultProbeTimeoutSeconds = 10;
    public const int MaximumProbeTimeoutSeconds = 120;
    public const int MinimumExpectedStatusCode = 100;
    public const int DefaultExpectedStatusCode = 200;
    public const int MaximumExpectedStatusCode = 599;
    public const int MinimumTransitionThreshold = 1;
    public const int DefaultFailureThreshold = 3;
    public const int DefaultRecoveryThreshold = 2;
    public const int MaximumTransitionThreshold = 20;
    public const int MinimumHistoryHours = 1;
    public const int MaximumHistoryHours = 168;
    public const int MaximumErrorLength = 500;

    public const int BackendProbeTickSeconds = 15;
    public const int MinimumBackendProbeTickSeconds = 5;
    public const int MaximumBackendProbeTickSeconds = 300;
    public const int BackendProbeConcurrency = 4;
    public const int AgentProbeTickSeconds = 15;
    public const int AgentProbeConfigRefreshSeconds = 60;
    public const int MaximumBufferedProbeResults = 5000;

    public const int DefaultRawRetentionDays = 7;
    public const int MinimumRawRetentionDays = 1;
    public const int MaximumRawRetentionDays = 90;
    public const int DefaultHourlyRetentionDays = 90;
    public const int MinimumHourlyRetentionDays = 1;
    public const int MaximumHourlyRetentionDays = 3650;
    public const int DefaultTraceRetentionDays = 14;
    public const int MinimumTraceRetentionDays = 1;
    public const int MaximumTraceRetentionDays = 90;
    public const int DefaultAggregationLookbackHours = 6;
    public const int DefaultStartupCatchUpHours = 48;
    public const int MinimumAggregationLookbackHours = 1;
    public const int MaximumAggregationLookbackHours = 168;

    public const int MaximumMetricNamesPerApp = 200;
    public const int MaximumPointsPerRequest = 5000;
    public const int MaximumSamplesPerMinutePerApp = 30_000;
    public const int MaximumSeriesPoints = 1500;
    public const int MaximumMetricHistoryHours = 2160;
    /// <summary>Bounds the attribute-combination picker for a metric name (S-TECH-VOLM: a high-cardinality
    /// attribute must not make the series picker itself unbounded).</summary>
    public const int MaximumMetricAttributeGroups = 50;

    public const int DefaultVisitorRetentionDays = 35;
    public const int MinimumVisitorRetentionDays = 7;
    public const int MaximumVisitorRetentionDays = 365;
    public const int MaximumVisitorHistoryDays = 90;

    public const int DefaultAnalyticsDetailedRetentionDays = 30;
    public const int MinimumAnalyticsDetailedRetentionDays = 7;
    public const int MaximumAnalyticsDetailedRetentionDays = 90;
    public const int DefaultAnalyticsSessionRetentionDays = 90;
    public const int MinimumAnalyticsSessionRetentionDays = 30;
    public const int MaximumAnalyticsSessionRetentionDays = 180;
    public const int DefaultAnalyticsAggregateRetentionMonths = 25;
    public const int MinimumAnalyticsAggregateRetentionMonths = 13;
    public const int MaximumAnalyticsAggregateRetentionMonths = 37;
    public const int DefaultAnalyticsRejectionRetentionDays = 7;
    public const int MinimumAnalyticsRejectionRetentionDays = 1;
    public const int MaximumAnalyticsRejectionRetentionDays = 14;
    public const int DefaultAnalyticsSessionTimeoutMinutes = 30;
    public const int MinimumAnalyticsSessionTimeoutMinutes = 5;
    public const int MaximumAnalyticsSessionTimeoutMinutes = 120;
    public const int MaximumAnalyticsBatchSize = 100;
    public const int MaximumAnalyticsRoutesPerApp = 500;
    public const long DefaultAnalyticsStorageBudgetBytes = 104_857_600;

    /// <summary>Caps accepted audience events over a rolling hour before storage-budget eviction is
    /// allowed; exceeding it refuses the batch without deleting rows. The 5000-event allowance
    /// accommodates normal concurrent visits while bounding how quickly a flood can roll stored
    /// history.</summary>
    public const int DefaultAnalyticsRollingEventsPerHour = 5_000;
    public const int MinimumAnalyticsRollingEventsPerHour = 100;
    public const int MaximumAnalyticsRollingEventsPerHour = 1_000_000;
    public const int MaximumIngestKeyLifetimeDays = 90;
    public const int DefaultIngestKeyOverlapDays = 7;
    public const int MaximumIngestKeyOverlapDays = 7;
}
