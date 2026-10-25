// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// Backend black-box prober for off-fleet apps (<c>ServerId == null</c>): apps hosted outside the
/// managed fleet whose public URL the backend probes directly. On-fleet apps are probed by their agent.
/// Probes only public URLs - the <see cref="ProbeHttpClientName"/> client
/// enforces the SSRF guard at connect time.
/// </summary>
public sealed class AppProbeService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AppProbeService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    /// <summary>Named HttpClient for backend black-box probing of off-fleet apps (public URLs only).</summary>
    public const string ProbeHttpClientName = "app-probe";

    // Only the live colour runs it (decision of 2026-10-02, PostgresLeaderLease).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:app-probe", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        var enabled = configuration.GetValue("AppMonitoring:BackendProbe:Enabled", true);
        if (!enabled)
        {
            logger.LogInformation("Backend app probing disabled via configuration; AppProbeService idle.");
            return;
        }

        // Base cadence: we tick at the smallest allowed probe interval and per-app due-time gating
        // (LastCheckedAt + interval) decides which apps are actually probed each tick.
        var tickSeconds = Math.Clamp(
            configuration.GetValue("AppMonitoring:BackendProbe:TickSeconds", AppMonitoringDefaults.BackendProbeTickSeconds),
            AppMonitoringDefaults.MinimumBackendProbeTickSeconds,
            AppMonitoringDefaults.MaximumBackendProbeTickSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(tickSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProbeDueAppsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during backend app probing tick");
            }
        }
    }

    internal async Task ProbeDueAppsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppMonitoringRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IAppMonitoringService>();

        var apps = await repo.GetBackendProbedAppsAsync(ct).ConfigureAwait(false);
        if (apps.Count == 0)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var due = apps
            .Where(a => a.ProbeUrl is not null
                && (a.LastCheckedAt is null || now >= a.LastCheckedAt.Value.AddSeconds(a.ProbeIntervalSeconds)))
            .ToList();
        if (due.Count == 0)
            return;

        var results = new List<AppProbeResultDto>(due.Count);
        using var gate = new SemaphoreSlim(AppMonitoringDefaults.BackendProbeConcurrency);
        var tasks = due.Select(async app =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ProbeOneAsync(app.Id, app.ProbeUrl!, app.ExpectedStatusCode, app.ProbeTimeoutSeconds, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));

        if (results.Count > 0)
            await service.IngestProbeResultsAsync(results, ct).ConfigureAwait(false);
    }

    private async Task<AppProbeResultDto> ProbeOneAsync(int appId, string url, int expectedStatus, int timeoutSeconds, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ProbeHttpClientName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
            timeoutSeconds,
            AppMonitoringDefaults.MinimumProbeTimeoutSeconds,
            AppMonitoringDefaults.MaximumProbeTimeoutSeconds)));

        var timestamp = timeProvider.GetUtcNow().UtcDateTime;
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();
            var statusCode = (int)response.StatusCode;
            return new AppProbeResultDto
            {
                MonitoredAppId = appId,
                Timestamp = timestamp,
                IsUp = statusCode == expectedStatus,
                ResponseTimeMs = (int)sw.ElapsedMilliseconds,
                StatusCode = statusCode,
                Error = statusCode == expectedStatus ? null : $"Unexpected status {statusCode} (expected {expectedStatus})"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            sw.Stop();
            var reason = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested
                ? "Probe timed out"
                : ex.Message;
            return AppProbeResultDto.Failure(
                appId, timestamp, (int)sw.ElapsedMilliseconds, reason);
        }
    }
}
