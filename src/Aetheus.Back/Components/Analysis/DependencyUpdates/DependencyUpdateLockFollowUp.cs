// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Shared.Components.DependencyUpdates;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// R-6: the end of the lock refresh run of an automatic update. A successful run hands back the lock files; the owner it ran as is
/// checked again (still the pipeline's owner, still existing, active and allowed to run it), the files must hold the target version,
/// then they are committed on the update's branch (never on the default branch: the branch is the one the update wrote) and the CI
/// starts on that commit, its QA after it, as for any update. Any other end fails the update with its reason; the branch stays.
/// </summary>
internal sealed class DependencyUpdateLockFollowUp(
    DependencyUpdateRepository repository,
    IDependencyUpdateGit git,
    IDependencyUpdateLockRefresh lockRefresh,
    DependencyUpdateCiLaunch ciLaunch,
    TimeProvider timeProvider,
    ILogger<DependencyUpdateLockFollowUp> logger) : IDomainEventHandler<PipelineRunCompletedEvent>
{
    private const int MaxReasonLength = 2048;

    /// <summary>How long a row may wait without a run to look at (none recorded, or none found) before the sweep gives up on it: a launch
    /// that is still being recorded by another pass is not a lost one.</summary>
    private static readonly TimeSpan LostRunGrace = TimeSpan.FromMinutes(15);

    public async Task HandleAsync(PipelineRunCompletedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (await repository.FindOfLockRunAsync(domainEvent.PipelineRunId, ct).ConfigureAwait(false) is not { } update) return;
        await FinishAsync(update, domainEvent, ct).ConfigureAwait(false);
    }

    /// <summary>R-29: the sweep of each pass. A row left in <see cref="DependencyUpdateStatus.LockRefreshing"/> because the completion event
    /// of its run was lost (a restart, a handler that never ran) is finished here exactly as that event would have: a run now over is
    /// handled with its real status; a run that is still going is left alone; a run that no longer exists, or that was never recorded
    /// on the row, fails the row with that reason once <see cref="LostRunGrace"/> has passed (it can never complete).</summary>
    public async Task ResumeAsync(CancellationToken ct)
    {
        var waiting = await repository.GetLockRefreshingAsync(ct).ConfigureAwait(false);
        if (waiting.Count == 0) return;
        var statuses = await repository.GetRunStatusesAsync([.. waiting.Select(update => update.LockPipelineRunId).OfType<int>()], ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var update in waiting)
        {
            try
            {
                await ResumeOneAsync(update, statuses, now, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Mandatory catch: one stuck row must not stop the others, nor the pass that follows the sweep; it is found again next pass.
                repository.DiscardUnsavedChanges();
                logger.LogError(exception, "Dependency update {UpdateId}: resuming its lock refresh failed; it stays waiting", update.Id);
            }
        }
    }

    private async Task ResumeOneAsync(DependencyUpdate update, IReadOnlyDictionary<int, PipelineStatus> statuses, DateTime now, CancellationToken ct)
    {
        if (update.LockPipelineRunId is { } runId && statuses.TryGetValue(runId, out var status))
        {
            if (status.IsTerminal()) await FinishAsync(update, new PipelineRunCompletedEvent(runId, status), ct).ConfigureAwait(false);
        }
        else if (now - update.UpdatedAt >= LostRunGrace)
        {
            await FailAsync(update, update.LockPipelineRunId is { } lost
                ? $"the lock refresh run {lost} no longer exists."
                : "no lock refresh run was recorded for the update.", ct).ConfigureAwait(false);
            logger.LogWarning("Dependency update {UpdateId}: its lock refresh run is lost", update.Id);
        }
    }

    private async Task FinishAsync(DependencyUpdate update, PipelineRunCompletedEvent domainEvent, CancellationToken ct)
    {
        string? failure;
        try
        {
            failure = await CommitLocksAsync(update, domainEvent, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Mandatory catch: the dispatcher of the event only logs what a handler throws, which would leave the row waiting forever.
            // The row gets a product text, never the raw message (a git error may carry an address).
            logger.LogError(exception, "Dependency update {UpdateId}: committing the lock files of run {RunId} failed", update.Id, domainEvent.PipelineRunId);
            failure = "an unexpected error stopped the commit; the server log has its detail.";
        }

        if (failure is not null)
        {
            await FailAsync(update, failure, ct).ConfigureAwait(false);
            logger.LogWarning("Dependency update {UpdateId}: lock refresh run {RunId} not committed", update.Id, domainEvent.PipelineRunId);
            return;
        }

        await ciLaunch.LaunchAsync(update, ct).ConfigureAwait(false);
        logger.LogInformation("Dependency update {UpdateId}: lock files of run {RunId} committed, CI run {CiRunId}",
            update.Id, domainEvent.PipelineRunId, update.CiPipelineRunId);
    }

    private async Task FailAsync(DependencyUpdate update, string failure, CancellationToken ct)
    {
        update.Status = DependencyUpdateStatus.Failed;
        update.Reason = Truncate($"The lock files were not committed on {update.Branch}: {failure}");
        update.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repository.SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns why the lock files are not committed, or null once the branch holds them (the row then says so).</summary>
    private async Task<string?> CommitLocksAsync(DependencyUpdate update, PipelineRunCompletedEvent run, CancellationToken ct)
    {
        if (run.Status != PipelineStatus.Success) return $"the run {run.PipelineRunId} of the lock refresh ended as {run.Status}.";
        if (update.LockPipelineId is not { } pipelineId || string.IsNullOrWhiteSpace(update.LockRefreshOwner))
            return "the update does not say which pipeline and owner refreshed its lock files.";
        if (await lockRefresh.RecheckAsync(pipelineId, update.LockRefreshOwner, ct).ConfigureAwait(false) is { } refusal) return refusal;

        var paths = update.LockFilesJson is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];
        if (paths.Count == 0) return "the update names no lock file to read.";
        var locks = await lockRefresh.ReadLocksAsync(run.PipelineRunId, paths, ct).ConfigureAwait(false);
        if (locks.Failure is not null) return locks.Failure;
        var resolution = CveRemediationResolved.Read(update.Ecosystem, update.PackageName, update.TargetVersion, locks.Files);
        if (resolution.Failure is not null) return resolution.Failure;

        var row = await git.FindRepositoryAsync(update.GitInternalRepoId, update.ProjectId, ct).ConfigureAwait(false);
        if (row is null) return "the repository of the update no longer exists.";
        var message = $"chore(deps): refresh the lock files for {update.PackageName} {update.TargetVersion}\n\n"
                      + $"Run {run.PipelineRunId} of {CveRemediationLockRun.PipelineName}, launched as its owner '{update.LockRefreshOwner}'. "
                      + "Written by Aetheus (automatic dependency update); production is never deployed by it.";
        // The base is the commit the update wrote: the lock files go on top of the bumped manifest, on the update's own branch.
        var written = await git.WriteAsync(row.Repository, update.Branch!, update.CommitSha!, locks.Files, message, ct).ConfigureAwait(false);
        if (written.Error is { } error) return error;

        update.CommitSha = written.CommitSha;
        update.Status = DependencyUpdateStatus.BranchPushed;
        update.Reason = null;
        update.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repository.SaveAsync(ct).ConfigureAwait(false);
        return null;
    }

    private static string Truncate(string text) => text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
}
