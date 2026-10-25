// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aetheus.WebAnalytics;

internal sealed class AnalyticsExportService(
    AnalyticsExportQueue queue,
    IHttpClientFactory httpClientFactory,
    AetheusWebAnalyticsOptions options,
    ILogger<AnalyticsExportService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    internal const string HttpClientName = "aetheus-web-analytics";

    private readonly AnalyticsIngestRefusal _refusal = new(timeProvider ?? TimeProvider.System);

    private static readonly Meter Meter = new("Aetheus.WebAnalytics.Exporter", "1.1.0");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("aetheus.analytics.export.failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var state = new ExportBatchState();
        var flushInterval = TimeSpan.FromMilliseconds(options.ExportIntervalMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await FlushExpiredAsync(state, flushInterval, stoppingToken).ConfigureAwait(false)) continue;
            using var waitCts = CreateWaitToken(state, flushInterval, stoppingToken);
            var ready = await WaitForEventsAsync(state, waitCts.Token, stoppingToken).ConfigureAwait(false);
            if (ready is null) continue;
            if (!ready.Value) break;
            DrainQueue(state);
            if (state.Events.Count == 100) await FlushAsync(state, stoppingToken).ConfigureAwait(false);
        }
        if (state.Events.Count > 0 && !stoppingToken.IsCancellationRequested)
            await ExportAsync(state.Events, stoppingToken).ConfigureAwait(false);
    }

    private async Task<bool> FlushExpiredAsync(
        ExportBatchState state,
        TimeSpan interval,
        CancellationToken ct)
    {
        if (state.Events.Count == 0 || state.StartedAt is not { } startedAt
            || Stopwatch.GetElapsedTime(startedAt) < interval) return false;
        await FlushAsync(state, ct).ConfigureAwait(false);
        return true;
    }

    private static CancellationTokenSource CreateWaitToken(
        ExportBatchState state,
        TimeSpan interval,
        CancellationToken stoppingToken)
    {
        var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (state.Events.Count == 0 || state.StartedAt is not { } startedAt) return waitCts;
        var remaining = interval - Stopwatch.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero) waitCts.CancelAfter(remaining);
        return waitCts;
    }

    private async Task<bool?> WaitForEventsAsync(
        ExportBatchState state,
        CancellationToken waitToken,
        CancellationToken stoppingToken)
    {
        try
        {
            return await queue.WaitToReadAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && state.Events.Count > 0)
        {
            await FlushAsync(state, stoppingToken).ConfigureAwait(false);
            return null;
        }
    }

    private void DrainQueue(ExportBatchState state)
    {
        while (state.Events.Count < 100 && queue.TryRead(out var analyticsEvent))
        {
            state.StartedAt ??= Stopwatch.GetTimestamp();
            state.Events.Add(analyticsEvent!);
        }
    }

    private async Task FlushAsync(ExportBatchState state, CancellationToken ct)
    {
        await ExportAsync(state.Events, ct).ConfigureAwait(false);
        state.Events.Clear();
        state.StartedAt = null;
    }

    internal async Task ExportAsync(List<AnalyticsExportEvent> batch, CancellationToken ct)
    {
        var key = options.IngestKey;
        if (_refusal.IsSuspended(key))
        {
            // The key was refused for good: the batch is dropped (counted) without a request.
            Failed.Add(batch.Count, new KeyValuePair<string, object?>("reason", "key_refused"));
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.IngestEndpoint)
            {
                Content = JsonContent.Create(batch)
            };
            request.Headers.Add("x-aetheus-ingest-key", key);
            request.Headers.TryAddWithoutValidation("User-Agent", EmitterDescription());
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            ObserveResponse(response.StatusCode, key, batch.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Failed.Add(batch.Count, new KeyValuePair<string, object?>("reason", "transport"));
            logger.LogWarning(exception, "Aetheus Web Analytics export failed; {Count} events were dropped", batch.Count);
        }
    }

    private void ObserveResponse(System.Net.HttpStatusCode status, string key, int count)
    {
        if (status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            Failed.Add(count, new KeyValuePair<string, object?>("reason", "http_rejected"));
            // The key itself is never written; the endpoint and the emitter let the operator find the
            // application that still holds a stale key.
            if (_refusal.NoteRefused(key))
                logger.LogWarning(
                    "Aetheus Web Analytics: {Endpoint} refused the ingest key (HTTP {Status}). The key is no longer valid for this application (rotated, expired, or from another environment). Exports are suspended for {PeriodMinutes} minutes, then tried once again; set the current key and restart the application to resume now. Emitter {Emitter}",
                    options.IngestEndpoint?.GetLeftPart(UriPartial.Path),
                    (int)status,
                    AnalyticsIngestRefusal.Backoff.TotalMinutes,
                    EmitterDescription());
            return;
        }

        if (!((int)status is >= 200 and < 300))
        {
            Failed.Add(count, new KeyValuePair<string, object?>("reason", "http_rejected"));
            return;
        }

        if (_refusal.NoteAccepted())
            logger.LogInformation("Aetheus Web Analytics: the ingest key is accepted again; exports resumed");
    }

    /// <summary>The <c>User-Agent</c> token that names this emitter in Aetheus' refusal log.</summary>
    internal string EmitterDescription()
    {
        var site = new string((options.SiteId ?? string.Empty)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '-')
            .Take(64)
            .ToArray());
        var version = typeof(AnalyticsExportService).Assembly.GetName().Version?.ToString(3) ?? "0";
        return $"Aetheus.WebAnalytics/{version} (site {site}; application {options.ApplicationId})";
    }

    private sealed class ExportBatchState
    {
        public List<AnalyticsExportEvent> Events { get; } = new(100);
        public long? StartedAt { get; set; }
    }
}
