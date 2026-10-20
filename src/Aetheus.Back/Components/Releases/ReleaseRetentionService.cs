// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Configuration;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Releases;

/// <summary>
/// Recette R1-72, decision D2: releases older than <c>Retention:ReleaseDays</c> (30 days by default) are
/// archived every maintenance tick, by batches, on the leader backend only: the release and its commit
/// stay, its artifacts, files and runs go. What is old, what is protected and what archiving deletes is
/// <see cref="ReleaseRetentionRepository"/>'s rule; each sweep that archives something writes one audit
/// entry. Running it again archives nothing more: an archived release is no longer a candidate, a
/// protected one stays.
/// </summary>
public sealed class ReleaseRetentionService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<ReleaseRetentionService> logger,
    IPostgresLeaderLease leaderLease) : BackgroundService
{
    internal const string LeaseName = "aetheus:release-retention";

    internal const int DefaultRetentionDays = 30;

    /// <summary>Releases per archive transaction.</summary>
    internal const int BatchSize = 100;

    /// <summary>Archive transactions per sweep, so one sweep stays bounded; the next tick continues.</summary>
    internal const int MaxBatchesPerSweep = 20;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease.RunAsLeaderAsync(LeaseName, RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BackendRuntimeDefaults.MaintenanceInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ArchiveExpiredReleasesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during release retention");
            }
        }
    }

    internal async Task<int> ArchiveExpiredReleasesAsync(CancellationToken ct)
    {
        // Same bounds as the pipeline run retention: at least a day, at most about ten years.
        var retentionDays = Math.Clamp(configuration.GetValue("Retention:ReleaseDays", DefaultRetentionDays), 1, 3650);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-retentionDays);

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IReleaseRetentionRepository>();

        var archived = 0;
        for (var batch = 0; batch < MaxBatchesPerSweep; batch++)
        {
            var candidates = await repository.GetExpiredReleaseIdsAsync(cutoff, now, BatchSize, ct).ConfigureAwait(false);
            if (candidates.Count == 0) break;
            var archivedInBatch = await repository.ArchiveExpiredAsync(candidates, cutoff, now, ct).ConfigureAwait(false);
            archived += archivedInBatch;
            // Every candidate became protected in between (another colour, a deployment): stop
            // rather than select the same rows again; the next tick looks afresh.
            if (archivedInBatch == 0) break;
        }

        if (archived == 0) return 0;
        logger.LogInformation("Release retention: archived {Count} releases older than {Days} days", archived, retentionDays);
        await scope.ServiceProvider.GetRequiredService<IAuditService>().LogAsync(
            "ReleaseRetentionArchived", "Release", null,
            string.Create(CultureInfo.InvariantCulture, $"archived={archived}; retention={retentionDays} d; cutoff={cutoff:O}"),
            ct).ConfigureAwait(false);
        return archived;
    }
}
