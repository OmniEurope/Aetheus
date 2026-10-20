// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.RateLimiting;

namespace Aetheus.Back.Components.AppMonitoring.Ingest;

/// <summary>
/// Public OTLP/HTTP ingestion surface (ADR-021 phases 2-4). Authenticated by a per-app ingestion key in
/// the <c>x-aetheus-ingest-key</c> header (not JWT). Accepts standard OTLP/HTTP protobuf and keeps
/// OTLP/JSON as a compatibility format. Rate-limited per key, tight body cap. Telemetry content is
/// never written to the backend logs.
/// </summary>
[ApiController]
[Route("api/ingest/otlp/v1")]
[AllowAnonymous]
[EnableRateLimiting("otlp-ingest")]
[RequestSizeLimit(1024 * 1024)]
public sealed class IngestController(
    IIngestService ingest,
    IVisitorIngestService visitorIngest,
    IAppWebAnalyticsService webAnalytics,
    IIngestConfigurationService configuration) : ControllerBase
{
    private const string KeyHeader = "x-aetheus-ingest-key";

    /// <summary> The configuration the <c>Aetheus.Telemetry</c> package reads with its ingest key,
    /// the metric send interval of the application's project. 401 for an unknown key, like the
    /// exports. </summary>
    [HttpGet("configuration")]
    public async Task<ActionResult<IngestConfigurationDto>> Configuration(CancellationToken ct)
    {
        var appId = await ResolveRequestAppIdAsync(ct).ConfigureAwait(false);
        if (appId is null)
            return Unauthorized();
        var result = await configuration.GetAsync(appId.Value, ct).ConfigureAwait(false);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("metrics")]
    public Task<IActionResult> Metrics(CancellationToken ct) =>
        HandleAsync(OtlpJsonParser.ParseMetrics, OtlpProtobufParser.ParseMetrics,
            (appId, points) => ingest.IngestMetricsAsync(appId, points, ct), "rejectedDataPoints",
            dropped => new OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceResponse
            {
                PartialSuccess = dropped > 0 ? new OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsPartialSuccess
                {
                    RejectedDataPoints = dropped,
                    ErrorMessage = "Some data was dropped by ingestion caps."
                } : null
            }, ct);

    [HttpPost("logs")]
    public Task<IActionResult> Logs(CancellationToken ct) =>
        HandleAsync(OtlpJsonParser.ParseLogs, OtlpProtobufParser.ParseLogs,
            (appId, records) => ingest.IngestLogsAsync(appId, records, ct), "rejectedLogRecords",
            dropped => new OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceResponse
            {
                PartialSuccess = dropped > 0 ? new OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsPartialSuccess
                {
                    RejectedLogRecords = dropped,
                    ErrorMessage = "Some data was dropped by ingestion caps."
                } : null
            }, ct);

    [HttpPost("traces")]
    public Task<IActionResult> Traces(CancellationToken ct) =>
        HandleAsync(OtlpJsonParser.ParseErrors, OtlpProtobufParser.ParseErrors,
            (appId, errors) => ingest.IngestErrorsAsync(appId, errors, ct), "rejectedSpans",
            dropped => new OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceResponse
            {
                PartialSuccess = dropped > 0 ? new OpenTelemetry.Proto.Collector.Trace.V1.ExportTracePartialSuccess
                {
                    RejectedSpans = dropped,
                    ErrorMessage = "Some data was dropped by ingestion caps."
                } : null
            }, ct);

    [HttpPost("/api/ingest/visitors")]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> Visitor([FromBody] AppVisitorIngestRequest request, CancellationToken ct)
    {
        var appId = await ResolveRequestAppIdAsync(ct).ConfigureAwait(false);
        if (appId is null)
            return Unauthorized();

        await visitorIngest.RecordAsync(appId.Value, request.VisitorId, ct).ConfigureAwait(false);
        // Do not disclose whether the digest was new: callers get no cross-request tracking oracle.
        return NoContent();
    }

    [HttpPost("/api/ingest/web-analytics/v1/events")]
    [RequestSizeLimit(256 * 1024)]
    public async Task<IActionResult> WebAnalytics(
        [FromBody] List<AppWebAnalyticsIngestEvent> events,
        CancellationToken ct)
    {
        var appId = await ResolveRequestAppIdAsync(ct).ConfigureAwait(false);
        if (appId is null)
            return Unauthorized();
        if (events.Count == 0)
            return BadRequest("At least one analytics event is required.");

        var outcome = await webAnalytics.IngestAsync(appId.Value, events, ct).ConfigureAwait(false);
        return Accepted(new
        {
            outcome.Accepted,
            outcome.Replayed,
            outcome.Rejected
        });
    }

    // Pass missing keys through the service refusal path and its deduplicated warning;
    // request-level 401 logging remains at Debug.
    private Task<int?> ResolveRequestAppIdAsync(CancellationToken ct) =>
        ingest.ResolveAppIdAsync(Request.Headers[KeyHeader].FirstOrDefault() ?? string.Empty, ct);

    private async Task<IActionResult> HandleAsync<T>(
        Func<JsonDocument, IReadOnlyList<T>> jsonParser,
        Func<byte[], IReadOnlyList<T>> protobufParser,
        Func<int, IReadOnlyList<T>, Task<IngestOutcome>> handler,
        string rejectedField,
        Func<int, IMessage> protobufResponse,
        CancellationToken ct)
    {
        var appId = await ResolveRequestAppIdAsync(ct);
        if (appId is null)
            return Unauthorized();

        var contentType = Request.ContentType ?? string.Empty;
        if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                return BadRequest("Malformed OTLP/JSON payload.");
            }

            using (doc)
            {
                var outcome = await handler(appId.Value, jsonParser(doc));
                return JsonResponse(outcome, rejectedField);
            }
        }

        if (contentType.StartsWith("application/x-protobuf", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/protobuf", StringComparison.OrdinalIgnoreCase))
        {
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer, ct);
            IReadOnlyList<T> items;
            try
            {
                items = protobufParser(buffer.ToArray());
            }
            catch (InvalidProtocolBufferException)
            {
                return BadRequest("Malformed OTLP/protobuf payload.");
            }

            var outcome = await handler(appId.Value, items);
            return File(protobufResponse(outcome.Dropped).ToByteArray(), "application/x-protobuf");
        }

        return StatusCode(StatusCodes.Status415UnsupportedMediaType,
            "Supported OTLP/HTTP content types are application/x-protobuf and application/json.");
    }

    private IActionResult JsonResponse(IngestOutcome outcome, string rejectedField)
    {
        // OTLP success response: empty object, or partialSuccess when caps dropped part of the batch.
        if (outcome.Dropped > 0)
            return Ok(new Dictionary<string, object>
            {
                ["partialSuccess"] = new Dictionary<string, object>
                {
                    [rejectedField] = outcome.Dropped,
                    ["errorMessage"] = "Some data was dropped by ingestion caps."
                }
            });
        return Ok(new { });
    }
}
