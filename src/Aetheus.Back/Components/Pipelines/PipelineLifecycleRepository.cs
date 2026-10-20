// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

// Pipeline lifecycle queries: active-run checks, stuck-run reconcile (the run retention lives in
// PipelineRunRetentionRepository, the webhook/schedule trigger queries in PipelineAutoTriggerRepository,
// the approval queries in PipelineApprovalRepository).
internal sealed class PipelineLifecycleRepository(AppDbContext db, TimeProvider timeProvider)
{
    public async Task<bool> IsStepRetryEligibleAsync(
        int pipelineRunId,
        int stepRunId,
        CancellationToken ct = default)
    {
        var scannerKey = await db.Set<ServerTask>().AsNoTracking()
            .Where(task => task.PipelineRunId == pipelineRunId
                && task.PipelineStepRunId == stepRunId
                && task.Operation == OperationKind.PipelineRunScanner)
            .OrderByDescending(task => task.Id)
            .Select(task => task.Command)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (scannerKey is null) return true;

        var report = await db.AnalysisReports.AsNoTracking()
            .Where(item => item.PipelineRunId == pipelineRunId && item.ScannerKey == scannerKey)
            .OrderByDescending(item => item.Id)
            .Select(item => new { item.Status, item.ErrorMessage })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (report is null) return true;
        if (report.Status == AnalysisReportStatus.Unavailable)
            return ContainsTransientMarker(report.ErrorMessage);
        return report.Status == AnalysisReportStatus.Error && ContainsTransientMarker(report.ErrorMessage);
    }

    private static bool ContainsTransientMarker(string? message) =>
        message is not null && new[] { "network", "download", "registry", "temporar", "429", "502", "503", "504" }
            .Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public async Task<bool> TryTransitionPipelineRunStatusAsync(
        int runId, PipelineStatus expectedStatus, PipelineStatus newStatus, CancellationToken ct = default)
    {
        var completedAt = newStatus.IsTerminal()
            ? timeProvider.GetUtcNow().UtcDateTime
            : (DateTime?)null;

        if (db.Database.IsRelational())
        {
            var rows = await db.PipelineRuns
                .Where(r => r.Id == runId && r.Status == expectedStatus)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, newStatus)
                    .SetProperty(r => r.CompletedAt, completedAt), ct)
                .ConfigureAwait(false);
            var tracked = db.ChangeTracker.Entries<PipelineRun>().FirstOrDefault(e => e.Entity.Id == runId);
            if (tracked is not null) tracked.State = EntityState.Detached;
            return rows == 1;
        }

        var run = await db.PipelineRuns
            .FirstOrDefaultAsync(r => r.Id == runId && r.Status == expectedStatus, ct)
            .ConfigureAwait(false);
        if (run is null) return false;
        run.Status = newStatus;
        run.CompletedAt = completedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<int> ResetFailedStepRunsAsync(int runId, CancellationToken ct = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;

        // Failure finalization cancels the downstream tail. Reopen it with the failed step so a
        // partial retry cannot finalize green while required stages remain Cancelled.
        var failedSteps = await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId
                && (s.Status == TaskExecutionStatus.Failed
                    || s.Status == TaskExecutionStatus.Timeout
                    || s.Status == TaskExecutionStatus.Cancelled))
            .ToListAsync(ct).ConfigureAwait(false);

        // A stage whose artifacts could not be collected succeeded, so nothing above reopens it, and its
        // failed collection kept every retry from ending Success. It is replayed with the retry: the
        // artifact exists only once the stage has produced it again, the run's cleanup having taken the
        // workspace it was to be collected from.
        var uncollectedStages = await PipelineRunStatusRepository
            .GetStagesWithFailedCollectionAsync(db, runId, ct).ConfigureAwait(false);
        if (uncollectedStages.Count > 0)
            failedSteps.AddRange(await db.PipelineStepRuns
                .Where(s => s.PipelineRunId == runId
                    && uncollectedStages.Contains(s.StageName)
                    && s.Status == TaskExecutionStatus.Success)
                .ToListAsync(ct).ConfigureAwait(false));
        if (failedSteps.Count == 0) return 0;

        // Retries replay System:Prepare because an agent may have lost its workspace. User stages
        // remain gated on preparation so the retried step has its sources.
        var prepareSteps = await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId
                && s.IsSystem
                && s.StageName == PipelineRunService.SystemPrepareStage
                && s.Status == TaskExecutionStatus.Success)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var step in failedSteps.Concat(prepareSteps))
        {
            step.Status = TaskExecutionStatus.Pending;
            step.ExitCode = null;
            step.FailureCode = null;
            step.FailureReason = null;
            step.StartedAt = null;
            step.CompletedAt = null;
        }

        var run = await db.PipelineRuns.FindAsync([runId], ct).ConfigureAwait(false);
        if (run is not null)
        {
            run.Status = PipelineStatus.Running;
            run.CompletedAt = null;
            var variables = PipelineRunHelpers.DeserializeResolvedVariables(run.AdditionalVariablesJson);
            variables.Remove(PipelineRunService.CancellationRequestedVariable);
            variables.Remove(PipelineRunService.CancelledByVariable);
            variables.Remove(PipelineRunService.CancellationReasonVariable);
            run.AdditionalVariablesJson = JsonSerializer.Serialize(variables);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        return failedSteps.Count;
    }

    /// <summary>F3: the version of the release this run published, or null if it published none.
    /// An own-read over the shared <c>Releases</c> table (same pattern as
    /// <see cref="PipelineRunGradeAggregation"/>'s candidate-grade lookup) rather than a dependency on
    /// the Releases module, so <c>PipelineRunCompletedDownstreamHandler</c> can resolve an
    /// <c>on_success.release: latest</c> selector without Pipelines depending on Releases -
    /// <c>DependencyCycleAuditTests</c> refuses that edge (Releases already depends on Pipelines).</summary>
    public async Task<string?> FindPublishedReleaseVersionByRunIdAsync(int pipelineRunId, CancellationToken ct = default)
    {
        return await db.Releases
            .AsNoTracking()
            .Where(r => r.PipelineRunId == pipelineRunId)
            .Select(r => r.Version)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    // A trigger step waits on a child run and has NO
    // ServerTask, so TaskTimeoutService never sees it; if the child's completion event was lost (backend
    // restart, missed hook) the step hangs Running forever. The sweeper re-checks these; the age filter
    // keeps it from racing the normal event handler on freshly-dispatched triggers.
    public async Task<List<PipelineStepRun>> GetStuckRunningTriggerStepsAsync(DateTime startedBefore, CancellationToken ct = default)
        => await db.PipelineStepRuns
            .AsNoTracking()
            .Where(s => s.Status == TaskExecutionStatus.Running
                        && s.TriggeredRunId != null
                        && s.StartedAt != null
                        && s.StartedAt < startedBefore)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> DidStagesAllSucceedAsync(int runId, IReadOnlyCollection<string> stageNames, CancellationToken ct = default)
    {
        var statuses = await db.PipelineStepRuns
            .AsNoTracking()
            .Where(s => s.PipelineRunId == runId && stageNames.Contains(s.StageName))
            .Select(s => s.Status)
            .ToListAsync(ct).ConfigureAwait(false);
        return statuses.Count > 0 && statuses.All(status => status == TaskExecutionStatus.Success);
    }

    public async Task<List<int>> GetStalledSchedulableRunIdsAsync(DateTime startedBefore, CancellationToken ct = default)
        => await db.PipelineRuns
            .AsNoTracking()
            .Where(r => r.Status == PipelineStatus.Running
                        && r.StartedAt < startedBefore
                        // At least one step still needs scheduling.
                        && r.StepRuns.Any(s => s.Status == TaskExecutionStatus.Pending)
                        // Nothing can produce the next normal completion callback.
                        && !r.StepRuns.Any(s => s.Status == TaskExecutionStatus.Assigned
                                                || s.Status == TaskExecutionStatus.Running)
                        // Every workspace run owns a deferred cleanup task from the start. While it is
                        // merely Pending it cannot drive normal scheduling, so it must not hide a stalled
                        // run from recovery. Once assigned/running, however, its callback is in flight.
                        && !r.Tasks.Any(t => (t.Status == TaskExecutionStatus.Pending && !t.IsDeferredCleanup)
                                             || t.Status == TaskExecutionStatus.Assigned
                                             || t.Status == TaskExecutionStatus.Running))
            .Select(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<List<int>> GetStalledCancellationRunIdsAsync(DateTime startedBefore, CancellationToken ct = default)
        => await db.PipelineRuns
            .AsNoTracking()
            .Where(r => r.Status == PipelineStatus.Running
                        && r.StartedAt < startedBefore
                        && r.AdditionalVariablesJson.Contains(PipelineRunService.CancellationRequestedVariable)
                        // A pending deferred cleanup is deliberately ignored: reapplying cancellation
                        // releases orphan Running steps, then the scheduler dispatches that cleanup.
                        && !r.Tasks.Any(t => (t.Status == TaskExecutionStatus.Pending && !t.IsDeferredCleanup)
                                             || t.Status == TaskExecutionStatus.Assigned
                                             || t.Status == TaskExecutionStatus.Running))
            .Select(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<List<int>> GetStuckRunningRunIdsAsync(DateTime startedBefore, CancellationToken ct = default)
        => await db.PipelineRuns
            .AsNoTracking()
            .Where(r => r.Status == PipelineStatus.Running
                        && r.StartedAt < startedBefore
                        // No task still in flight: every task is terminal, so nothing will drive the run.
                        && !r.Tasks.Any(t => t.Status == TaskExecutionStatus.Pending
                                             || t.Status == TaskExecutionStatus.Assigned
                                             || t.Status == TaskExecutionStatus.Running)
                        // Not legitimately waiting on a child pipeline (that case is the trigger-step sweep).
                        && !r.StepRuns.Any(s => s.Status == TaskExecutionStatus.Running && s.TriggeredRunId != null)
                        // Nor on an environment another run holds: that wait ends with the holder, whose
                        // own tasks time out if it dies (decision of 2026-10-02).
                        && !(r.WaitingReason != null && r.WaitingReason.Contains(PipelineEnvironmentLockGate.WaitMarker)))
            .Select(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Every run still in flight, whatever its pipeline. Feeds the agents' workspace reaper, so the
    /// set has to be complete: a run missing from it has its sources deleted underneath it.
    /// WaitingForApproval counts as in flight - such a run writes nothing for days and still resumes.
    /// </summary>
    public async Task<List<int>> GetAllActiveRunIdsAsync(CancellationToken ct = default)
    {
        return await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Status == PipelineStatus.Running
                          || run.Status == PipelineStatus.Pending
                          || run.Status == PipelineStatus.WaitingForApproval)
            .Select(run => run.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<PipelineRunDto>> GetActiveRunsAsync(
        List<int>? accessiblePipelineIds = null, int? projectId = null, CancellationToken ct = default)
    {
        var query = db.PipelineRuns.AsNoTracking()
            .Where(run => run.Status == PipelineStatus.Running
                || run.Status == PipelineStatus.Pending
                || run.Status == PipelineStatus.WaitingForApproval);
        if (accessiblePipelineIds is not null)
            query = query.Where(run => accessiblePipelineIds.Contains(run.PipelineId));
        if (projectId.HasValue)
            query = query.Where(run => run.Pipeline.ProjectId == projectId.Value);

        var runs = await query
            .OrderByDescending(run => run.StartedAt)
            .Take(100)
            .Select(PipelineRunDtoMapper.RunListProjection)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var hydrated = await PipelineAdvisorySteps.ApplyToListAsync(
            db, runs.Select(PipelineRunDtoMapper.HydrateListWarnings).ToList(), ct).ConfigureAwait(false);
        var graded = await PipelineRunGradeAggregation.ApplyAsync(db, hydrated, ct).ConfigureAwait(false);
        return await PipelineRunRepositoryLinks.ApplyAsync(db, graded, ct).ConfigureAwait(false);
    }

    public async Task<List<PipelineRunDto>> GetRecentRunsAsync(
        List<int>? accessiblePipelineIds = null, int? projectId = null, int? serverId = null,
        CancellationToken ct = default)
    {
        var query = db.PipelineRuns.AsNoTracking().AsQueryable();
        if (accessiblePipelineIds is not null)
            query = query.Where(run => accessiblePipelineIds.Contains(run.PipelineId));
        if (projectId.HasValue)
            query = query.Where(run => run.Pipeline.ProjectId == projectId.Value);
        if (serverId.HasValue)
            query = query.Where(run => run.StepRuns.Any(step => step.ServerId == serverId.Value));

        var runs = await query
            .OrderByDescending(run => run.StartedAt)
            .Take(20)
            .Select(PipelineRunDtoMapper.RunListProjection)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var hydrated = await PipelineAdvisorySteps.ApplyToListAsync(
            db, runs.Select(PipelineRunDtoMapper.HydrateListWarnings).ToList(), ct).ConfigureAwait(false);
        var graded = await PipelineRunGradeAggregation.ApplyAsync(db, hydrated, ct).ConfigureAwait(false);
        return await PipelineRunRepositoryLinks.ApplyAsync(db, graded, ct).ConfigureAwait(false);
    }
}
