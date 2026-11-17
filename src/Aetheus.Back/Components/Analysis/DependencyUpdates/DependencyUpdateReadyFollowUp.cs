// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// R-30: what follows an update whose CI and QA both succeeded. On an internal repository the pull request from the update's branch into
/// the default branch is opened (one only: an open one from that branch is reused) in the platform's own representation of a merge
/// request, and the subscribers are notified; merging stays human. On a repository that mirrors an external host nothing is sent there
/// (no external API call, no push): the update records that the request has to be opened on the origin, and the subscribers are told.
/// The step is done once per update (<see cref="DependencyUpdate.ReadyHandledAt"/>), whichever of the QA completion event and the sweep
/// of the next pass arrives first and however many times a run completes.
/// </summary>
internal sealed class DependencyUpdateReadyFollowUp(
    DependencyUpdateRepository repository,
    IDependencyUpdateMergeRequests mergeRequests,
    DependencyUpdateNotices notices,
    TimeProvider timeProvider,
    ILogger<DependencyUpdateReadyFollowUp> logger) : IDomainEventHandler<PipelineRunCompletedEvent>
{
    private const int MaxReasonLength = 2048;

    public async Task HandleAsync(PipelineRunCompletedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (await repository.FindOfQaRunAsync(domainEvent.PipelineRunId, ct).ConfigureAwait(false) is not { } update) return;
        if (update.ReadyHandledAt is not null || domainEvent.Status != PipelineStatus.Success) return;
        var ciStatus = update.CiPipelineRunId is { } ciRun
            ? (await repository.GetRunStatusesAsync([ciRun], ct).ConfigureAwait(false)).GetValueOrDefault(ciRun)
            : (PipelineStatus?)null;
        if (ciStatus != PipelineStatus.Success) return;
        await HandleReadyAsync(update, ct).ConfigureAwait(false);
    }

    /// <summary>The sweep of each pass: an update whose QA completion event was lost (a restart, a handler that never ran) is finished here
    /// exactly as the event would have, once both of its runs are really over and successful.</summary>
    public async Task ResumeAsync(CancellationToken ct)
    {
        var waiting = await repository.GetAwaitingReadyAsync(ct).ConfigureAwait(false);
        if (waiting.Count == 0) return;
        var statuses = await repository.GetRunStatusesAsync(
            [.. waiting.SelectMany(update => new[] { update.CiPipelineRunId, update.QaPipelineRunId }).OfType<int>()], ct).ConfigureAwait(false);
        foreach (var update in waiting)
        {
            if (!IsGreen(statuses, update.CiPipelineRunId) || !IsGreen(statuses, update.QaPipelineRunId)) continue;
            try
            {
                await HandleReadyAsync(update, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Mandatory catch: one update that cannot be finished must not stop the others, nor the pass; it is found again next pass.
                repository.DiscardUnsavedChanges();
                logger.LogError(exception, "Dependency update {UpdateId}: its pull request could not be opened; it is retried at the next pass", update.Id);
            }
        }
    }

    private static bool IsGreen(IReadOnlyDictionary<int, PipelineStatus> statuses, int? runId) =>
        runId is { } id && statuses.TryGetValue(id, out var status) && status == PipelineStatus.Success;

    private async Task HandleReadyAsync(DependencyUpdate update, CancellationToken ct)
    {
        if (update.ReadyHandledAt is not null) return;
        var row = await mergeRequests.FindRepositoryAsync(update.GitInternalRepoId, update.ProjectId, ct).ConfigureAwait(false);
        if (row is null) return;

        DependencyUpdateReadyOutcome outcome;
        if (row.IsMirror)
        {
            outcome = DependencyUpdateReadyOutcome.PullRequestToOpenOnOrigin;
        }
        else
        {
            update.PullRequestId = await mergeRequests.OpenAsync(
                row.Repository, update.Branch!, Title(update), Description(update), ct).ConfigureAwait(false);
            update.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            // Saved before the notification: a retry finds the id (and the open pull request) instead of opening a second one.
            await repository.SaveAsync(ct).ConfigureAwait(false);
            outcome = DependencyUpdateReadyOutcome.PullRequestOpened;
        }

        await notices.ReadyAsync(update, outcome, ct).ConfigureAwait(false);
        if (row.IsMirror)
            Note(update, $"CI and QA of {update.Branch} succeeded: the merge request has to be opened on the origin of this mirror repository (nothing was sent there).");
        update.ReadyHandledAt = timeProvider.GetUtcNow().UtcDateTime;
        update.UpdatedAt = update.ReadyHandledAt.Value;
        await repository.SaveAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Dependency update {UpdateId}: CI and QA succeeded, {Outcome}", update.Id, outcome);
    }

    private static string Title(DependencyUpdate update) => $"Update {update.PackageName} to {update.TargetVersion} ({update.Advisory})";

    private static string Description(DependencyUpdate update) =>
        $"{update.Advisory}: {update.PackageName} moves from {update.FromVersion} to {update.TargetVersion}. Opened by Aetheus because the CI and the QA "
        + $"of {update.Branch} at {update.CommitSha} both succeeded."
        + (update.IsMajorUpgrade ? " This update crosses a major version: read the release notes before merging." : string.Empty)
        + " Review and merge by hand: nothing is merged, promoted or deployed automatically.";

    private static void Note(DependencyUpdate update, string note)
    {
        var text = string.IsNullOrWhiteSpace(update.Reason) ? note : $"{update.Reason} {note}";
        update.Reason = text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
    }
}
