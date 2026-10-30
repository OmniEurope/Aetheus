// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Git;

/// <inheritdoc cref="IGitCommitRecorder"/>
public sealed class GitCommitRecorder(
    IGitLightRepository lightRepo,
    IHubContext<GitRealtimeHub> hub,
    IMemoryCache cache,
    TimeProvider timeProvider,
    IGitDevelopBranchProvisioner developBranches,
    ILogger<GitCommitRecorder> logger) : IGitCommitRecorder
{
    public async Task RecordCommitAsync(int repositoryId, CancellationToken ct = default)
    {
        var firstCommit = await lightRepo.IsEmptyAsync(repositoryId, ct).ConfigureAwait(false);
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

        // The first commit of a repository is when an organization that asks for it gets its develop branch (R-76).
        if (firstCommit)
            await developBranches.EnsureAfterFirstCommitAsync(repositoryId, ct).ConfigureAwait(false);
    }
}
