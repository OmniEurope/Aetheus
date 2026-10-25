// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>A branch the server moved itself keeps the stored pipeline definitions in step with the
/// branch, exactly as a push does, and nothing more: no pipeline is triggered. Moving a branch onto a
/// commit that was already built and deployed (the post-deployment advance, ADR-044) must not re-run
/// the pipelines watching it, which is what <see cref="GitPushPipelineTriggerHandler"/> would do for
/// a push.</summary>
internal sealed class GitBranchMovedPipelineSyncHandler(PipelineDefinitionSynchronizer synchronizer)
    : IDomainEventHandler<GitBranchMovedEvent>
{
    public async Task HandleAsync(GitBranchMovedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        foreach (var update in domainEvent.RefUpdates)
            await synchronizer.SyncAsync(domainEvent, update, ct).ConfigureAwait(false);
    }
}
