// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Releases;

public sealed class ReleaseScanService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ReleaseScanService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    // Only the live colour runs it (decision of 2026-10-02, PostgresLeaderLease).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:release-scan", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("ReleaseScan:IntervalMinutes", 0);
        if (intervalMinutes <= 0)
        {
            logger.LogInformation("ReleaseScanService disabled (IntervalMinutes = 0 or not configured)");
            return;
        }

        var interval = TimeSpan.FromMinutes(intervalMinutes);
        logger.LogInformation("ReleaseScanService started with interval {Interval} min", intervalMinutes);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ScanPendingReleasesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in ReleaseScanService");
            }
        }
    }

    internal async Task ScanPendingReleasesAsync(CancellationToken ct)
    {
        List<Project> projects;
        IHubContext<ReleaseHub> releaseHub;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var projectRepo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            releaseHub = scope.ServiceProvider.GetRequiredService<IHubContext<ReleaseHub>>();
            projects = await projectRepo.GetAllProjectsWithRepoUrlAsync(ct).ConfigureAwait(false);
        }

        using var semaphore = new SemaphoreSlim(4);
        await Task.WhenAll(projects.Select(async project =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Each parallel task gets its own scope so DbContext is not shared across threads.
                await using var projectScope = scopeFactory.CreateAsyncScope();
                var releaseService = projectScope.ServiceProvider.GetRequiredService<IReleaseService>();

                var synced = await releaseService.SyncReleasesAsync(project.Id, ct).ConfigureAwait(false);
                if (synced.Count > 0)
                {
                    logger.LogInformation("ReleaseScan: {Count} releases found for project {Project}", synced.Count, project.Name);

                    await releaseHub.Clients.Groups([HubGroups.AllReleases, HubGroups.ProjectReleases(project.Id)])
                        .SendAsync("ReleasesUpdated", project.Id, synced, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ReleaseScan: failed for project {Project} (#{Id})", project.Name, project.Id);
            }
            finally { semaphore.Release(); }
        })).ConfigureAwait(false);
    }
}
