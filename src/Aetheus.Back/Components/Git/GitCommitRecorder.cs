// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Git;

/// <inheritdoc cref="IGitCommitRecorder"/>
public sealed class GitCommitRecorder(
    IGitLightRepository lightRepo,
    IHubContext<GitRealtimeHub> hub,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<GitCommitRecorder> logger) : IGitCommitRecorder
{
    public async Task RecordCommitAsync(int repositoryId, CancellationToken ct = default)
    {
        var projectId = await lightRepo.MarkCommittedAsync(
            repositoryId, timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (projectId is null) return;
        cache.Remove(ProjectCacheKeys.GitUpdate(projectId.Value));

        try
        {
            var group = hub.Clients.Group(GitRealtimeGroups.Repository(repositoryId));
            await group.SendAsync(GitRealtimeEvents.RepositoryChanged, repositoryId, ct).ConfigureAwait(false);
            await group.SendAsync(GitRealtimeEvents.BranchesChanged, repositoryId, ct).ConfigureAwait(false);
            await group.SendAsync(GitRealtimeEvents.CommitsChanged, repositoryId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory: the commit and the row are already written; a realtime hiccup must not turn
            // a saved pipeline into a reported failure. Open views refresh on their next load.
            logger.LogWarning(ex, "Failed to broadcast git realtime commit event for repository {Id}", repositoryId);
        }
    }
}
