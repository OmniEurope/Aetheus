// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// Read/manage surface for OTLP telemetry (ADR-021 phases 2-4): ingestion keys, metric series + thresholds,
/// logs, errors. RBAC is inherited from the monitored app's parent Project, like <see cref="AppMonitoringController"/>.
/// </summary>
[ApiController]
[Route("api/appmonitoring")]
[Authorize]
public sealed class AppTelemetryController(
    IAppTelemetryService telemetry,
    IAppWebAnalyticsService webAnalytics,
    IAppWebAnalyticsConfigurationService webAnalyticsConfiguration,
    IAppMonitoringService monitoring,
    IResourceAuthorizationService authz,
    IAppTelemetrySendIntervalService sendInterval) : ControllerBase
{
    // --- ingestion key (Write) ---

    [HttpPost("apps/{id:int}/ingest-key")]
    public async Task<ActionResult<IngestKeyResponse>> GenerateIngestKey(int id, CancellationToken ct)
    {
        if (await GateAsync(id, Permission.Write, ct) is { } fail) return fail;
        var key = await telemetry.GenerateIngestKeyAsync(id, ct);
        return key is null ? NotFound() : Ok(key);
    }

    [HttpDelete("apps/{id:int}/ingest-key")]
    public async Task<IActionResult> RevokeIngestKey(int id, CancellationToken ct)
    {
        if (await GateAsync(id, Permission.Write, ct) is { } fail) return fail;
        return await telemetry.RevokeIngestKeyAsync(id, ct) ? NoContent() : NotFound();
    }

    // --- metrics (Read) ---

    [HttpGet("apps/{id:int}/metrics/names")]
    public async Task<ActionResult<List<string>>> GetMetricNames(int id, CancellationToken ct)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        return Ok(await telemetry.GetMetricNamesAsync(id, ct));
    }

    [HttpGet("apps/{id:int}/metrics/series")]
    public async Task<ActionResult<MetricSeriesDto>> GetMetricSeries(
        int id,
        [FromQuery] string metric,
        [FromQuery] string? attributes = null,
        [FromQuery] int hours = 24,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metric))
            return BadRequest(new ApiError { Message = "metric query parameter is required." });
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        return Ok(await telemetry.GetMetricSeriesAsync(id, metric, attributes, hours, ct));
    }

    [HttpGet("apps/{id:int}/metrics/series-groups")]
    public async Task<ActionResult<List<string?>>> GetMetricSeriesGroups(
        int id, [FromQuery] string metric, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metric))
            return BadRequest(new ApiError { Message = "metric query parameter is required." });
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        return Ok(await telemetry.GetMetricSeriesGroupsAsync(id, metric, ct));
    }

    /// <summary>R-455: the application's per-route timings, as its Aetheus.Telemetry package last exported them.</summary>
    [HttpGet("apps/{id:int}/performance")]
    public async Task<ActionResult<AppPerformanceReportDto>> GetPerformance(int id, CancellationToken ct = default)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        return Ok(await telemetry.GetPerformanceAsync(id, ct));
    }

    /// <summary>PLAN-013 lot 1: the application's database size, as its Aetheus.Telemetry package last measured it.</summary>
    [HttpGet("apps/{id:int}/database")]
    public async Task<ActionResult<AppDatabaseSizeReportDto>> GetDatabaseSize(int id, CancellationToken ct = default)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        return Ok(await telemetry.GetDatabaseSizeAsync(id, ct));
    }

    /// <summary>The interval the server hands to the application's Aetheus.Telemetry package, and the one its
    /// recent samples show (a package older than 1.1.0 keeps its own).</summary>
    [HttpGet("apps/{id:int}/send-interval")]
    public async Task<ActionResult<AppTelemetrySendIntervalDto>> GetSendInterval(int id, CancellationToken ct = default)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        var interval = await sendInterval.GetAsync(id, ct);
        return interval is null ? NotFound() : Ok(interval);
    }

    // --- visitors ---

    [HttpGet("apps/{id:int}/visitors")]
    public async Task<ActionResult<AppVisitorSeriesDto>> GetVisitors(
        int id, [FromQuery] int days = 30, CancellationToken ct = default)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        return Ok(await telemetry.GetVisitorSeriesAsync(id, days, ct));
    }

    [HttpGet("apps/{id:int}/web-analytics")]
    public async Task<ActionResult<AppWebAnalyticsSummaryDto>> GetWebAnalytics(
        int id,
        [FromQuery] int days = 30,
        CancellationToken ct = default)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        var summary = await webAnalytics.GetSummaryAsync(id, days, ct);
        return summary is null ? NotFound() : Ok(summary);
    }

    [HttpPut("apps/{id:int}/web-analytics/configuration")]
    public async Task<ActionResult<AppWebAnalyticsConfigurationDto>> ConfigureWebAnalytics(
        int id,
        [FromBody] ConfigureAppWebAnalyticsRequest request,
        CancellationToken ct)
    {
        if (await GateAsync(id, Permission.Write, ct) is { } fail) return fail;
        try
        {
            var configured = await webAnalyticsConfiguration.ConfigureAsync(id, request, ct);
            return configured is null ? NotFound() : Ok(configured);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ApiError { Message = exception.Message });
        }
    }

    [HttpGet("apps/{id:int}/web-analytics/configuration")]
    public async Task<ActionResult<AppWebAnalyticsConfigurationDto>> GetWebAnalyticsConfiguration(
        int id,
        CancellationToken ct)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        var configured = await webAnalyticsConfiguration.GetAsync(id, ct);
        return configured is null ? NotFound() : Ok(configured);
    }

    [HttpPost("apps/{id:int}/web-analytics/rotate-key")]
    public async Task<ActionResult<AppWebAnalyticsConfigurationDto>> RotateWebAnalyticsKey(
        int id,
        CancellationToken ct)
    {
        if (await GateAsync(id, Permission.Write, ct) is { } fail) return fail;
        var configured = await webAnalyticsConfiguration.RotateKeyAsync(id, ct);
        return configured is null ? NotFound() : Ok(configured);
    }

    // --- thresholds ---

    [HttpGet("apps/{id:int}/thresholds")]
    public async Task<ActionResult<List<AppMetricThresholdDto>>> GetThresholds(int id, CancellationToken ct)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        return Ok(await telemetry.GetThresholdsAsync(id, ct));
    }

    [HttpPost("apps/{id:int}/thresholds")]
    public async Task<ActionResult<AppMetricThresholdDto>> CreateThreshold(int id, [FromBody] CreateMetricThresholdRequest request, CancellationToken ct)
    {
        if (await GateAsync(id, Permission.Write, ct) is { } fail) return fail;
        return Ok(await telemetry.CreateThresholdAsync(id, request, ct));
    }

    [HttpDelete("thresholds/{thresholdId:int}")]
    public async Task<IActionResult> DeleteThreshold(int thresholdId, CancellationToken ct)
    {
        var appId = await telemetry.GetThresholdAppIdAsync(thresholdId, ct);
        if (appId is null)
            return NotFound();
        if (await GateAsync(appId.Value, Permission.Write, ct) is { } fail) return fail;
        return await telemetry.DeleteThresholdAsync(thresholdId, ct) ? NoContent() : NotFound();
    }

    // --- logs (Read) ---

    [HttpGet("apps/{id:int}/logs")]
    public async Task<ActionResult<PaginatedResult<AppLogEntryDto>>> GetLogs(
        int id, [FromQuery] int hours = 24, [FromQuery] int? minSeverity = null,
        [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 100,
        [FromQuery, StringLength(50)] string? sortBy = null, [FromQuery] bool sortDescending = true,
        CancellationToken ct = default,
        [FromQuery(Name = "Filters"), MaxLength(PaginationRequest.MaxFilters)] List<GridFilter>? filters = null)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        // Recette R-358: the log grid's sort and header filters, checked against the allow-list of
        // AppLogQuery; an unknown key or an unparsable value is a 400.
        return Ok(await telemetry.GetLogsAsync(id, hours, minSeverity, search, page, pageSize, ct,
            filters is { Count: > 0 } ? filters : null, sortBy, sortDescending));
    }

    // --- errors (Read) ---

    [HttpGet("apps/{id:int}/errors")]
    public async Task<ActionResult<PaginatedResult<AppErrorEventDto>>> GetErrors(
        int id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery, StringLength(50)] string? sortBy = null, [FromQuery] bool sortDescending = true,
        CancellationToken ct = default,
        [FromQuery(Name = "Filters"), MaxLength(PaginationRequest.MaxFilters)] List<GridFilter>? filters = null)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        // The error grid's sort and header filters, checked against the allow-list of AppErrorQuery; an
        // unknown key or an unparsable value is a 400.
        return Ok(await telemetry.GetErrorsAsync(id, page, pageSize, ct,
            filters is { Count: > 0 } ? filters : null, sortBy, sortDescending));
    }

    /// <summary>The candidates of the error grid's exception-type filter: every type the app stored,
    /// not only those of the rows on screen.</summary>
    [HttpGet("apps/{id:int}/errors/exception-types")]
    public async Task<ActionResult<List<string>>> GetErrorExceptionTypes(int id, CancellationToken ct = default)
    {
        if (await GateAsync(id, Permission.Read, ct) is { } fail) return fail;
        return Ok(await telemetry.GetErrorExceptionTypesAsync(id, ct));
    }

    /// <summary>Resolves the app's parent Project and checks the required permission. Returns a failing
    /// result (NotFound/Forbid) to short-circuit, or null when access is granted.</summary>
    private async Task<ActionResult?> GateAsync(int appId, Permission required, CancellationToken ct)
    {
        var projectId = await monitoring.GetAppProjectIdAsync(appId, ct);
        if (projectId is null)
            return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, required, ct))
            return Forbid();
        return null;
    }
}
