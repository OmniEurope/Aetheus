// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Git.Events;

/// <summary>What every announcement of moved branches of an internal repository carries, whether a
/// client pushed them (<see cref="GitPushProcessedEvent"/>) or the server moved them itself
/// (<see cref="GitBranchMovedEvent"/>). Lets a subscriber read the new tips the same way for both
/// without the two events being interchangeable: only the first one is a push.</summary>
public interface IGitRefUpdatesEvent : IDomainEvent
{
    int ProjectId { get; }
    int GitRepoId { get; }
    string Slug { get; }
    string RepositoryName { get; }
    string? DefaultBranch { get; }

    /// <summary>Resolved, validated on-disk path of the bare repository.</summary>
    string DiskPath { get; }

    IReadOnlyList<GitRefUpdate> RefUpdates { get; }

    /// <summary>The user who pushed, when known. Carried because the push is handled off the request: a
    /// pushed pipeline definition makes its pusher the owner of its automated runs (RBAC audit, finding 3).</summary>
    string? PusherUsername { get; }
}
