// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.ExternalRepos;

/// <summary>
/// Scheduled refresh of external-repo mirrors.
/// Wakes once a minute and fetches every auto-sync connection whose fetch interval has elapsed.
/// </summary>
public sealed class ExternalRepoSyncService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ExternalRepoSyncService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = BackendRuntimeDefaults.SchedulerCheckInterval;

    // Only the live colour runs it (decision of 2026-10-02, PostgresLeaderLease).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:external-repo-sync", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SyncDueMirrorsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ExternalRepos] scheduled sync sweep failed");
            }
        }
    }

    internal async Task SyncDueMirrorsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var lightRepo = scope.ServiceProvider.GetRequiredService<IGitLightRepository>();
        var gitRepo = scope.ServiceProvider.GetRequiredService<IGitRepository>();
        var mirror = scope.ServiceProvider.GetRequiredService<IExternalRepoMirrorService>();
        var notifier = scope.ServiceProvider.GetRequiredService<IEntityChangeNotifier>();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var mirrors = await lightRepo.GetAllAsync(ct).ConfigureAwait(false);

        foreach (var mirrorRepo in mirrors)
        {
            try
            {
                await SyncIfDueAsync(mirrorRepo, now, lightRepo, gitRepo, mirror, notifier, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One failing mirror must not stop the refresh of the other mirrors.
                logger.LogError(ex, "[ExternalRepos] scheduled sync failed for repository {RepositoryId}", mirrorRepo.Id);
            }
        }
    }

    private static async Task SyncIfDueAsync(
        GitInternalRepo mirrorRepo, DateTime now, IGitLightRepository lightRepo, IGitRepository gitRepo,
        IExternalRepoMirrorService mirror, IEntityChangeNotifier notifier, CancellationToken ct)
    {
        if (mirrorRepo.GitConnectionId is null)
        {
            return;
        }

        var connection = await gitRepo.FindConnectionAsync(mirrorRepo.GitConnectionId.Value, ct).ConfigureAwait(false);
        if (connection is null || !connection.AutoSyncEnabled || connection.MirrorStatus == GitMirrorStatus.Syncing)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(connection.FetchIntervalMinutes, 1, 1440));
        var due = connection.LastFetchedAt is null || now - connection.LastFetchedAt.Value >= interval;
        if (!due)
        {
            return;
        }

        await mirror.SyncAsync(connection, mirrorRepo, ct).ConfigureAwait(false);
        await gitRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        await lightRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Project, connection.ProjectId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
    }
}
