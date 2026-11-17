// SPDX-License-Identifier: EUPL-1.2
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
    IPostgresLeaderLease leaderLease) : LeaderBackgroundService(leaderLease, "aetheus:app-probe")
{
    /// <summary>Named HttpClient for backend black-box probing of off-fleet apps (public URLs only).</summary>
    public const string ProbeHttpClientName = AppHttpProber.HttpClientName;

    private readonly AppHttpProber _prober = new(httpClientFactory, timeProvider);

    protected override async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
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
        var failureLevel = new BackgroundTickFailureLevel();

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProbeDueAppsAsync(stoppingToken).ConfigureAwait(false);
                failureLevel.Succeeded();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A database that restarts or cannot be reached is not a failure of the probe (R-2).
                logger.Log(failureLevel.For(ex, stoppingToken), ex, "Error during backend app probing tick");
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
                return await _prober.ProbeAsync(app.Id, app.ProbeUrl!, app.ExpectedStatusCode, app.ProbeTimeoutSeconds, ct)
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
}
