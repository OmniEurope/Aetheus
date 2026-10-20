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
    ILogger<AnalyticsExportService> logger) : BackgroundService
{
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

    private async Task ExportAsync(List<AnalyticsExportEvent> batch, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(AetheusWebAnalyticsServiceExtensions.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.IngestEndpoint)
            {
                Content = JsonContent.Create(batch)
            };
            request.Headers.Add("x-aetheus-ingest-key", options.IngestKey);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Failed.Add(batch.Count, new KeyValuePair<string, object?>("reason", "http_rejected"));
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

    private sealed class ExportBatchState
    {
        public List<AnalyticsExportEvent> Events { get; } = new(100);
        public long? StartedAt { get; set; }
    }
}
