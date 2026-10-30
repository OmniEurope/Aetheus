// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Git;

/// <summary>The push that creates the default branch of an internal repository is its first commit: it asks
/// <see cref="IGitDevelopBranchProvisioner"/> for the <c>develop</c> branch the organization wants (R-76). A later push
/// to the default branch, or to any other branch, never does.</summary>
public sealed class GitDevelopBranchPushHandler(IGitDevelopBranchProvisioner provisioner)
    : IDomainEventHandler<GitPushProcessedEvent>
{
    public async Task HandleAsync(GitPushProcessedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (string.IsNullOrEmpty(domainEvent.DefaultBranch)) return;
        var defaultReference = $"refs/heads/{domainEvent.DefaultBranch}";
        if (domainEvent.RefUpdates.Any(update => CreatesReference(update, defaultReference)))
            await provisioner.EnsureAfterFirstCommitAsync(domainEvent.GitRepoId, ct).ConfigureAwait(false);
    }

    private static bool CreatesReference(GitRefUpdate update, string reference) =>
        string.Equals(update.Reference, reference, StringComparison.Ordinal)
        && !IsNull(update.NewObjectId)
        && IsNull(update.OldObjectId);

    private static bool IsNull(string objectId) => objectId.Length == 0 || objectId.All(character => character == '0');
}
