// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aetheus.Agent.Core.Services;

/// <summary>
/// ADR-021 phase 1: black-box availability prober for apps hosted on this agent's server. Pulls its
/// probe list from the backend, runs each probe at its interval against localhost/LAN (a plain outbound
/// HttpClient, zero elevation), and reports results by batch with a bounded in-memory re-queue - dropped
/// results on a prolonged backend outage are reported honestly, never silently.
/// </summary>
public sealed class AppProbeWorker(
    IServerApiClient apiClient,
    IEnrollmentService enrollment,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<AppProbeWorker> logger) : BackgroundService
{
    public const string ProbeHttpClientName = "AppProbe";

    private List<AppProbeConfigDto> _configs = [];
    private readonly Dictionary<int, DateTime> _nextDue = new();
    private readonly List<AppProbeResultDto> _buffer = [];
    private DateTime _lastConfigRefresh = DateTime.MinValue;
    private int _droppedSinceLastReport;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!enrollment.IsEnrolled && !stoppingToken.IsCancellationRequested)
            await Task.Delay(AgentRuntimeDefaults.StartupRetryDelay, timeProvider, stoppingToken).ConfigureAwait(false);

        // On the injected clock (the system clock in production), so the loop runs on a test clock too.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(AppMonitoringDefaults.AgentProbeTickSeconds), timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "App probe tick failed, will retry next interval");
            }
        }
    }

    internal async Task RunTickAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (now - _lastConfigRefresh >= TimeSpan.FromSeconds(AppMonitoringDefaults.AgentProbeConfigRefreshSeconds))
        {
            try
            {
                _configs = await apiClient.GetAppProbesAsync(ct).ConfigureAwait(false);
                _lastConfigRefresh = now;
                var live = _configs.Select(c => c.MonitoredAppId).ToHashSet();
                foreach (var stale in _nextDue.Keys.Where(k => !live.Contains(k)).ToList())
                    _nextDue.Remove(stale);
            }
            catch (Exception ex)
            {
                // Keep the previous config set: a backend blip must not stop probing.
                logger.LogWarning(ex, "Failed to refresh app probe config, reusing last known set");
            }
        }

        var due = _configs
            .Where(c => !string.IsNullOrWhiteSpace(c.ProbeUrl)
                && (!_nextDue.TryGetValue(c.MonitoredAppId, out var next) || now >= next))
            .ToList();

        if (due.Count > 0)
        {
            using var gate = new SemaphoreSlim(4);
            var probeTasks = due.Select(async config =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await ProbeOneAsync(config, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });

            var results = await Task.WhenAll(probeTasks).ConfigureAwait(false);
            foreach (var config in due)
                _nextDue[config.MonitoredAppId] = now.AddSeconds(Math.Max(config.ProbeIntervalSeconds, AppMonitoringDefaults.AgentProbeTickSeconds));

            AppendToBuffer(results);
        }

        await FlushAsync(ct).ConfigureAwait(false);
    }

    private void AppendToBuffer(IEnumerable<AppProbeResultDto> results)
    {
        _buffer.AddRange(results);
        var overflow = _buffer.Count - AppMonitoringDefaults.MaximumBufferedProbeResults;
        if (overflow > 0)
        {
            _buffer.RemoveRange(0, overflow);
            _droppedSinceLastReport += overflow;
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_buffer.Count == 0)
            return;

        if (_droppedSinceLastReport > 0)
        {
            logger.LogWarning("Dropped {Count} app probe result(s) while the backend was unreachable (buffer cap {Cap})",
                _droppedSinceLastReport, AppMonitoringDefaults.MaximumBufferedProbeResults);
            _droppedSinceLastReport = 0;
        }

        var batch = _buffer.ToList();
        try
        {
            await apiClient.ReportAppProbeResultsAsync(batch, ct).ConfigureAwait(false);
            _buffer.Clear();
        }
        catch (Exception ex)
        {
            // Best-effort: keep the batch buffered (bounded) and retry next tick.
            logger.LogWarning(ex, "Failed to report app probe results, will retry next interval");
        }
    }

    private async Task<AppProbeResultDto> ProbeOneAsync(AppProbeConfigDto config, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ProbeHttpClientName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
            config.ProbeTimeoutSeconds,
            AppMonitoringDefaults.MinimumProbeTimeoutSeconds,
            AppMonitoringDefaults.MaximumProbeTimeoutSeconds)));

        var timestamp = timeProvider.GetUtcNow().UtcDateTime;
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.ProbeUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();
            var statusCode = (int)response.StatusCode;
            return new AppProbeResultDto
            {
                MonitoredAppId = config.MonitoredAppId,
                Timestamp = timestamp,
                IsUp = statusCode == config.ExpectedStatusCode,
                ResponseTimeMs = (int)sw.ElapsedMilliseconds,
                StatusCode = statusCode,
                Error = statusCode == config.ExpectedStatusCode
                    ? null
                    : $"Unexpected status {statusCode} (expected {config.ExpectedStatusCode})"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            sw.Stop();
            var reason = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested ? "Probe timed out" : ex.Message;
            return AppProbeResultDto.Failure(
                config.MonitoredAppId, timestamp, (int)sw.ElapsedMilliseconds, reason);
        }
    }
}
