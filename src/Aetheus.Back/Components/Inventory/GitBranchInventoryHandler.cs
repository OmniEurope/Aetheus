// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Inventory;

/// <summary>A default branch that moved, by a push or by a merged pull request, asks for a new inventory
/// of its project. Any other branch says nothing about what the project ships.</summary>
public sealed class GitBranchInventoryHandler(IProjectInventoryTrigger trigger)
    : IDomainEventHandler<GitPushProcessedEvent>, IDomainEventHandler<GitBranchMovedEvent>
{
    public Task HandleAsync(GitPushProcessedEvent domainEvent, CancellationToken ct = default) => RequestAsync(domainEvent);

    public Task HandleAsync(GitBranchMovedEvent domainEvent, CancellationToken ct = default) => RequestAsync(domainEvent);

    private Task RequestAsync(IGitRefUpdatesEvent moved)
    {
        ArgumentNullException.ThrowIfNull(moved);
        var defaultReference = $"refs/heads/{moved.DefaultBranch}";
        if (moved.RefUpdates.Any(update => string.Equals(update.Reference, defaultReference, StringComparison.Ordinal)))
            trigger.Request(moved.ProjectId);
        return Task.CompletedTask;
    }
}
