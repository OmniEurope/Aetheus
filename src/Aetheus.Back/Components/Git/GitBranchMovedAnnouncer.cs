// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Git;

public interface IGitBranchMovedAnnouncer
{
    /// <summary>Announces that <paramref name="branch"/> of <paramref name="repo"/> now points at
    /// <paramref name="newSha"/>. A failing subscriber never surfaces here.</summary>
    /// <param name="previousSha">The tip before the move; null or empty when unknown or when the
    /// branch did not exist.</param>
    Task AnnounceAsync(
        GitInternalRepo repo, string diskPath, string branch, string? previousSha, string newSha,
        CancellationToken ct = default);
}

/// <summary>Raises <see cref="GitBranchMovedEvent"/> for the branches this server moves without a
/// push. Awaited rather than queued: the caller (a pipeline step, a merge) reports its own outcome
/// only once the subscribers have synchronized, and the dispatcher already isolates their failures.</summary>
public sealed class GitBranchMovedAnnouncer(IDomainEventDispatcher domainEvents) : IGitBranchMovedAnnouncer
{
    public Task AnnounceAsync(
        GitInternalRepo repo, string diskPath, string branch, string? previousSha, string newSha,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return domainEvents.DispatchAsync(
            new GitBranchMovedEvent(
                repo.ProjectId, repo.Id, repo.Slug, repo.Name, repo.DefaultBranch, diskPath,
                [new GitRefUpdate($"refs/heads/{branch}", newSha, previousSha ?? string.Empty)]),
            ct);
    }
}
