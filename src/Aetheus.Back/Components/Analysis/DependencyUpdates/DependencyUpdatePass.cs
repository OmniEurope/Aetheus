// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>PLAN-007: the updates follow each matching pass of the watch (the daily pass of its sources, and the passes an inventory
/// change starts). Nothing is carried from one pass to the next: the candidates are read from the stored matches. Each pass first
/// resumes the updates whose lock refresh run ended without its completion event reaching them (recette R-29), then writes the new
/// updates, then does what R-30 asks of the others: the pull request of an update whose CI and QA succeeded without its event reaching
/// us, and the retirement of the branch of an update whose vulnerability this pass closed.</summary>
internal sealed class DependencyUpdatePass(
    DependencyUpdateService updates,
    DependencyUpdateLockFollowUp lockFollowUp,
    DependencyUpdateReadyFollowUp readyFollowUp,
    DependencyUpdateBranchRetirement retirement)
    : IDomainEventHandler<VulnerabilityMatchesChangedEvent>
{
    public async Task HandleAsync(VulnerabilityMatchesChangedEvent domainEvent, CancellationToken ct = default)
    {
        await lockFollowUp.ResumeAsync(ct).ConfigureAwait(false);
        await updates.RunPassAsync(ct).ConfigureAwait(false);
        await readyFollowUp.ResumeAsync(ct).ConfigureAwait(false);
        await retirement.RunAsync(ct).ConfigureAwait(false);
    }
}
