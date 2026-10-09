// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.AppMonitoring;

public interface IAppTelemetryService
{
    Task<IngestKeyResponse?> GenerateIngestKeyAsync(int appId, CancellationToken ct = default);
    Task<bool> RevokeIngestKeyAsync(int appId, CancellationToken ct = default);

    Task<List<string>> GetMetricNamesAsync(int appId, CancellationToken ct = default);
    Task<List<string?>> GetMetricSeriesGroupsAsync(int appId, string metricName, CancellationToken ct = default);
    Task<MetricSeriesDto> GetMetricSeriesAsync(int appId, string metricName, string? attributesJson, int hours, CancellationToken ct = default);
    Task<AppVisitorSeriesDto> GetVisitorSeriesAsync(int appId, int days, CancellationToken ct = default);
    /// <summary>R-455: the per-route timings the application last exported through Aetheus.Telemetry.</summary>
    Task<AppPerformanceReportDto> GetPerformanceAsync(int appId, CancellationToken ct = default);
    /// <summary>PLAN-013 lot 1: the database size the application last measured through Aetheus.Telemetry.</summary>
    Task<AppDatabaseSizeReportDto> GetDatabaseSizeAsync(int appId, CancellationToken ct = default);

    Task<List<AppMetricThresholdDto>> GetThresholdsAsync(int appId, CancellationToken ct = default);
    Task<AppMetricThresholdDto> CreateThresholdAsync(int appId, CreateMetricThresholdRequest request, CancellationToken ct = default);
    Task<int?> GetThresholdAppIdAsync(int thresholdId, CancellationToken ct = default);
    Task<bool> DeleteThresholdAsync(int thresholdId, CancellationToken ct = default);

    Task<PaginatedResult<AppLogEntryDto>> GetLogsAsync(int appId, int hours, int? minSeverity, string? search, int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = true);
    Task<PaginatedResult<AppErrorEventDto>> GetErrorsAsync(int appId, int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = true);
    Task<List<string>> GetErrorExceptionTypesAsync(int appId, CancellationToken ct = default);
}
