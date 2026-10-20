// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Artifacts;

public sealed class ArtifactCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<ArtifactCleanupService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease operationLock,
    IChunkedArtifactUploadService chunkedUploads) : BackgroundService
{
    /// <summary>Only one backend holds the cleanup lease at a time.</summary>
    internal const string LeaseName = "aetheus:artifact-cleanup";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        operationLock.RunAsLeaderAsync(LeaseName, RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BackendRuntimeDefaults.MaintenanceInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CleanupExpiredArtifactsAsync(stoppingToken).ConfigureAwait(false);
                await PurgeUnreferencedFilesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during artifact cleanup");
            }
        }
    }

    /// <summary>How long an unfinished chunked upload keeps its parts on disk. A transfer of a
    /// gigabyte-scale artifact can legitimately span a long run, so the window is generous; what it
    /// bounds is the abandoned session, whose parts nothing else would ever delete.</summary>
    internal static readonly TimeSpan UnfinishedUploadLifetime = TimeSpan.FromHours(24);

    internal async Task CleanupExpiredArtifactsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IArtifactRepository>();
        var storage = scope.ServiceProvider.GetRequiredService<IArtifactStorageService>();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        PurgeAbandonedUploads();

        var expired = await repo.GetExpiredAsync(now, 100, ct).ConfigureAwait(false);
        if (expired.Count == 0) return;

        var deletedCount = 0;
        foreach (var artifact in expired)
        {
            try
            {
                if (await RemoveExpiredAsync(repo, storage, artifact, now, ct).ConfigureAwait(false))
                    deletedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Mandatory: one artifact whose row delete is refused must not stop the cleanup of the
                // others. Its row and file both stay, so nothing is lost and the next pass retries.
                logger.LogWarning(ex,
                    "Artifact cleanup: could not remove expired artifact {ArtifactId}; its row and file are kept",
                    artifact.Id);
            }
        }

        logger.LogInformation("Artifact cleanup: deleted {Count} expired artifacts", deletedCount);
    }

    private async Task<bool> RemoveExpiredAsync(
        IArtifactRepository repo, IArtifactStorageService storage, PipelineArtifact artifact, DateTime now, CancellationToken ct)
    {
        var removed = false;
        await operationLock.RunSerializedAsync(
            ArtifactRetentionLock.For(artifact.Id),
            async lockToken =>
            {
                // GetExpiredAsync is only a candidate selection. A checkpoint resume can acquire a
                // retention lease after that query, so revalidate while holding the same distributed
                // lock used by resume before deleting any bytes.
                if (await repo.HasActiveRetentionLeaseAsync(artifact.Id, now, lockToken).ConfigureAwait(false))
                    return;
                removed = await ArtifactPayloadRemoval.RemoveRowThenFileAsync(
                    repo, storage, artifact, logger, lockToken).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return removed;
    }

    /// <summary>Recette R1-73: how long a payload file no artifact row references stays on disk.</summary>
    internal static readonly TimeSpan UnreferencedFileLifetime = TimeSpan.FromDays(30);

    /// <summary>Files deleted per sweep, so one sweep stays bounded; the next tick continues.</summary>
    internal const int MaxUnreferencedFilesPerSweep = 500;

    /// <summary>
    /// Recette R1-73: a payload file that no artifact row references can no longer be downloaded,
    /// deployed or restored by anything; after 30 days it is deleted. Every protection of the expired
    /// cleanup applies through the rows: a file a row still records, whatever its retention, lease,
    /// release or pending delivery, is never touched, and the row lookup is repeated just before each
    /// delete. Such files are left behind when a row disappears without the cleanup, for instance with
    /// the pipeline run it belonged to.
    /// </summary>
    internal async Task<int> PurgeUnreferencedFilesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IArtifactRepository>();
        var storage = scope.ServiceProvider.GetRequiredService<IArtifactStorageService>();
        var inventory = scope.ServiceProvider.GetRequiredService<ArtifactFileInventory>();

        var candidates = inventory.ListFilesWrittenBefore(
            timeProvider.GetUtcNow().UtcDateTime - UnreferencedFileLifetime);
        if (candidates.Count == 0) return 0;

        // Compared as the full paths they designate, ignoring case: a spelling difference keeps the file.
        var referenced = (await repo.GetAllFilePathsAsync(ct).ConfigureAwait(false))
            .Select(inventory.Key)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deleted = 0;
        foreach (var file in candidates.Where(file => !referenced.Contains(inventory.Key(file)!)))
        {
            if (deleted >= MaxUnreferencedFilesPerSweep) break;
            if (await repo.IsFilePathReferencedAsync(file, ct).ConfigureAwait(false)) continue;
            try
            {
                await storage.DeleteArtifactAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mandatory: one locked or unreadable file must not stop the rest of the sweep.
                logger.LogWarning(ex, "Failed to delete unreferenced artifact file {Path}", file);
                continue;
            }
            deleted++;
        }

        if (deleted == 0) return 0;
        logger.LogInformation("Artifact cleanup: deleted {Count} artifact files no artifact references", deleted);
        await scope.ServiceProvider.GetRequiredService<IAuditService>().LogAsync(
            "ArtifactUnreferencedFilesPurged", "PipelineArtifact", null,
            string.Create(CultureInfo.InvariantCulture,
                $"deleted={deleted}; unreferenced for more than {UnreferencedFileLifetime.TotalDays} d"),
            ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Parts of uploads that were begun and never completed. Best-effort and never fatal: an
    /// unreadable session directory must not stop the artifact cleanup that runs beside it.
    /// </summary>
    private void PurgeAbandonedUploads()
    {
        try
        {
            var purged = chunkedUploads.PurgeExpired(UnfinishedUploadLifetime);
            if (purged > 0)
                logger.LogInformation("Artifact cleanup: removed {Count} abandoned chunked uploads", purged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to purge abandoned chunked uploads");
        }
    }
}

internal static class ArtifactRetentionLock
{
    internal static string For(int artifactId) => $"artifact-retention:{artifactId}";
}
