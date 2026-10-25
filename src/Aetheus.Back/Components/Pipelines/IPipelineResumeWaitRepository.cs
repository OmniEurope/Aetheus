// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What <see cref="IPipelineResumeWaitRepository.ReopenAwaitingRunAsync"/> found.</summary>
public enum ResumeReopenOutcome
{
    /// <summary>The run is Running again and the stages to replay are Pending.</summary>
    Reopened,

    /// <summary>The run was not (or no longer) waiting for a resume; nothing changed.</summary>
    NotWaiting,

    /// <summary>The run still waits but its window has elapsed: the reconcile sweep closes it.</summary>
    Expired
}

/// <summary>
/// Recette R-39: the rows behind the wait for a resume after a missed deployment confirmation. A run
/// parked here keeps everything it ran (its workspace, its environment lock, its outputs); a resume
/// replays the stages from the start of the deployment, and a window that elapses closes the run.
/// <see cref="IPipelineRepository"/> composes this port and delegates to the same queries.
/// </summary>
public interface IPipelineResumeWaitRepository
{
    /// <summary>Moves a Running run to <see cref="PipelineStatus.AwaitingResume"/> with its deadline, in
    /// one conditional update. False when the run was no longer Running.</summary>
    Task<bool> TryParkRunAwaitingResumeAsync(int runId, DateTime deadline, CancellationToken ct = default);

    /// <summary>Re-opens a waiting run whose window is still open at <paramref name="now"/>: every step
    /// of <paramref name="stageNames"/> goes back to Pending (outcome, outputs and timings cleared), the
    /// run goes back to Running, all in one transaction. The deadline is kept: the run has one window,
    /// counted from its first missed confirmation, and a later miss waits only until that same deadline.</summary>
    Task<ResumeReopenOutcome> ReopenAwaitingRunAsync(
        int runId, IReadOnlyCollection<string> stageNames, DateTime now, CancellationToken ct = default);

    /// <summary>Waiting runs whose deadline is at or before <paramref name="now"/>, oldest first.</summary>
    Task<List<int>> GetExpiredResumeWaitRunIdsAsync(DateTime now, CancellationToken ct = default);

    /// <summary>Moves a waiting run whose deadline has elapsed back to Running so the scheduler can
    /// close it. False when it was not waiting or its window is still open.</summary>
    Task<bool> TryCloseExpiredResumeWaitAsync(int runId, DateTime now, CancellationToken ct = default);

    /// <summary>The stages of a run holding a step that failed and did not continue on error.</summary>
    Task<List<string>> GetBlockingFailedStageNamesAsync(int runId, CancellationToken ct = default);
}

internal sealed class PipelineResumeWaitRepository(AppDbContext db) : IPipelineResumeWaitRepository
{
    public async Task<bool> TryParkRunAwaitingResumeAsync(int runId, DateTime deadline, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            var rows = await db.PipelineRuns
                .Where(run => run.Id == runId && run.Status == PipelineStatus.Running)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.Status, PipelineStatus.AwaitingResume)
                    .SetProperty(run => run.ResumeDeadline, (DateTime?)deadline)
                    .SetProperty(run => run.CompletedAt, (DateTime?)null), ct)
                .ConfigureAwait(false);
            DetachRun(runId);
            return rows == 1;
        }

        var tracked = await db.PipelineRuns
            .FirstOrDefaultAsync(run => run.Id == runId && run.Status == PipelineStatus.Running, ct)
            .ConfigureAwait(false);
        if (tracked is null) return false;
        tracked.Status = PipelineStatus.AwaitingResume;
        tracked.ResumeDeadline = deadline;
        tracked.CompletedAt = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ResumeReopenOutcome> ReopenAwaitingRunAsync(
        int runId, IReadOnlyCollection<string> stageNames, DateTime now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stageNames);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;

        // The status is the concurrency check, in the same statement as the move: a second resume, a
        // cancellation or the expiry sweep that moved the run first leaves nothing to update here.
        if (!await TryMoveWaitingRunToRunningAsync(runId, now, ct).ConfigureAwait(false))
        {
            var status = await db.PipelineRuns.AsNoTracking().Where(r => r.Id == runId)
                .Select(r => (PipelineStatus?)r.Status).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return status == PipelineStatus.AwaitingResume ? ResumeReopenOutcome.Expired : ResumeReopenOutcome.NotWaiting;
        }

        var steps = await db.PipelineStepRuns
            .Where(step => step.PipelineRunId == runId && stageNames.Contains(step.StageName))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var step in steps)
        {
            step.Status = TaskExecutionStatus.Pending;
            step.ExitCode = null;
            step.FailureCode = null;
            step.FailureReason = null;
            step.OutputVariablesJson = null;
            step.StartedAt = null;
            step.CompletedAt = null;
            step.TaskId = null;
            step.SkippedCondition = null;
            step.SkippedConditionVariablesJson = null;
            step.SkippedReason = null;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        return ResumeReopenOutcome.Reopened;
    }

    private async Task<bool> TryMoveWaitingRunToRunningAsync(int runId, DateTime now, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var rows = await db.PipelineRuns
                .Where(run => run.Id == runId && run.Status == PipelineStatus.AwaitingResume
                    && run.ResumeDeadline != null && run.ResumeDeadline > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.Status, PipelineStatus.Running)
                    .SetProperty(run => run.CompletedAt, (DateTime?)null), ct)
                .ConfigureAwait(false);
            DetachRun(runId);
            return rows == 1;
        }

        var tracked = await db.PipelineRuns
            .FirstOrDefaultAsync(run => run.Id == runId && run.Status == PipelineStatus.AwaitingResume
                && run.ResumeDeadline != null && run.ResumeDeadline > now, ct)
            .ConfigureAwait(false);
        if (tracked is null) return false;
        tracked.Status = PipelineStatus.Running;
        tracked.CompletedAt = null;
        return true;
    }

    public async Task<List<int>> GetExpiredResumeWaitRunIdsAsync(DateTime now, CancellationToken ct = default) =>
        await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Status == PipelineStatus.AwaitingResume
                && run.ResumeDeadline != null && run.ResumeDeadline <= now)
            .OrderBy(run => run.ResumeDeadline)
            .Select(run => run.Id)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> TryCloseExpiredResumeWaitAsync(int runId, DateTime now, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            var rows = await db.PipelineRuns
                .Where(run => run.Id == runId && run.Status == PipelineStatus.AwaitingResume
                    && run.ResumeDeadline != null && run.ResumeDeadline <= now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.Status, PipelineStatus.Running), ct)
                .ConfigureAwait(false);
            DetachRun(runId);
            return rows == 1;
        }

        var tracked = await db.PipelineRuns
            .FirstOrDefaultAsync(run => run.Id == runId && run.Status == PipelineStatus.AwaitingResume
                && run.ResumeDeadline != null && run.ResumeDeadline <= now, ct)
            .ConfigureAwait(false);
        if (tracked is null) return false;
        tracked.Status = PipelineStatus.Running;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<List<string>> GetBlockingFailedStageNamesAsync(int runId, CancellationToken ct = default) =>
        await db.PipelineStepRuns.AsNoTracking()
            .Where(step => step.PipelineRunId == runId && !step.ContinueOnError
                && (step.Status == TaskExecutionStatus.Failed || step.Status == TaskExecutionStatus.Timeout))
            .Select(step => step.StageName)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

    private void DetachRun(int runId)
    {
        var tracked = db.ChangeTracker.Entries<PipelineRun>().FirstOrDefault(entry => entry.Entity.Id == runId);
        if (tracked is not null) tracked.State = EntityState.Detached;
    }
}
