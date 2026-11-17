// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// R-30: what follows an update whose vulnerability is closed (the match the update answered is resolved). On an internal repository the
/// update's branch is deleted (its pull request first closed when still open; a branch already gone is not an error) and the subscribers
/// are notified. On a repository that mirrors an external host the branch is never deleted nor is a deletion pushed to the origin: that
/// needs the user's agreement and the platform has no pending-action mechanism that fits, so the update records that the branch was left
/// and the subscribers are told, who delete it on the origin themselves. The step is done once per update
/// (<see cref="DependencyUpdate.BranchRetiredAt"/>); it runs at each pass of the vulnerability watch, which is what closes matches.
/// </summary>
internal sealed class DependencyUpdateBranchRetirement(
    DependencyUpdateRepository repository,
    IDependencyUpdateMergeRequests mergeRequests,
    DependencyUpdateNotices notices,
    TimeProvider timeProvider,
    ILogger<DependencyUpdateBranchRetirement> logger)
{
    private const int MaxReasonLength = 2048;

    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var update in await repository.GetResolvedToRetireAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await RetireAsync(update, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Mandatory catch: one branch that cannot be retired must not stop the others, nor the pass; it is found again next pass.
                repository.DiscardUnsavedChanges();
                logger.LogError(exception, "Dependency update {UpdateId}: its branch could not be retired; it is retried at the next pass", update.Id);
            }
        }
    }

    private async Task RetireAsync(DependencyUpdate update, CancellationToken ct)
    {
        var row = await mergeRequests.FindRepositoryAsync(update.GitInternalRepoId, update.ProjectId, ct).ConfigureAwait(false);
        if (row is null) return;

        DependencyUpdateClosedOutcome outcome;
        string? detail = null;
        if (row.IsMirror)
        {
            outcome = DependencyUpdateClosedOutcome.BranchKeptMirror;
            detail = $"The branch {update.Branch} was left: deleting it on the origin of this mirror repository needs your agreement.";
        }
        else
        {
            var retirement = await mergeRequests.RetireAsync(row.Repository, update.Branch!, update.PullRequestId, ct).ConfigureAwait(false);
            detail = retirement.Refusal;
            outcome = retirement.Refusal is not null ? DependencyUpdateClosedOutcome.BranchKeptProtected
                : retirement.Existed ? DependencyUpdateClosedOutcome.BranchDeleted
                : DependencyUpdateClosedOutcome.BranchAlreadyGone;
        }

        await notices.ClosedAsync(update, outcome, detail, ct).ConfigureAwait(false);
        if (outcome is DependencyUpdateClosedOutcome.BranchKeptMirror or DependencyUpdateClosedOutcome.BranchKeptProtected) Note(update, detail!);
        update.BranchRetiredAt = timeProvider.GetUtcNow().UtcDateTime;
        update.UpdatedAt = update.BranchRetiredAt.Value;
        await repository.SaveAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Dependency update {UpdateId}: the vulnerability is closed, {Outcome}", update.Id, outcome);
    }

    private static void Note(DependencyUpdate update, string note)
    {
        var text = string.IsNullOrWhiteSpace(update.Reason) ? note : $"{update.Reason} {note}";
        update.Reason = text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
    }
}
