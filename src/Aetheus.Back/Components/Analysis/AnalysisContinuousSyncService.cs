// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Analysis;

public sealed class AnalysisContinuousSyncService(
    IServiceScopeFactory scopeFactory,
    IOptions<DependencyTrackOptions> options,
    TimeProvider timeProvider,
    ILogger<AnalysisContinuousSyncService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private readonly DependencyTrackOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        if (leaderLease is null)
            await RunLeaderLoopAsync(stoppingToken).ConfigureAwait(false);
        else
            await leaderLease.RunAsLeaderAsync(
                "aetheus:analysis-continuous-sync", RunLeaderLoopAsync, stoppingToken).ConfigureAwait(false);
    }

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(Math.Clamp(_options.SyncIntervalMinutes, 5, 1440)), timeProvider);
        do
        {
            await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RunCycleSafelyAsync(CancellationToken ct)
    {
        try { await RunCycleAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "Dependency-Track continuous synchronization cycle failed"); }
    }

    internal async Task RunCycleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
        var client = scope.ServiceProvider.GetRequiredService<IDependencyTrackClient>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var projects = await repository.GetActiveTrackingProjectsAsync(100, ct).ConfigureAwait(false);
        foreach (var tracking in projects)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                var vulnerabilities = await client.GetVulnerabilitiesAsync(tracking.ExternalProjectId, ct).ConfigureAwait(false);
                AnalysisContinuousTracker.ApplySuccessfulSnapshot(tracking, vulnerabilities,
                    tracking.LastSbomReportId, isContinuous: true, now, out var observations);
                await repository.SaveTrackingSnapshotAsync(tracking, observations, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                tracking.SyncStatus = "Failed";
                tracking.LastError = exception.Message.Length <= 2000 ? exception.Message : exception.Message[..2000];
                tracking.LastSyncAt = now;
                tracking.UpdatedAt = now;
                await repository.SaveTrackingSnapshotAsync(tracking, [], ct).ConfigureAwait(false);
                await notifications.SendEventAsync("analysis.cve-sync.failed", new
                {
                    tracking.OrganizationId,
                    tracking.ProjectId,
                    tracking.LastSbomReportId,
                    tracking.SyncStatus,
                    tracking.LastError
                }, ct).ConfigureAwait(false);
                logger.LogError(exception, "Dependency-Track continuous synchronization failed for project {ProjectId}", tracking.ProjectId);
            }
        }
    }
}
