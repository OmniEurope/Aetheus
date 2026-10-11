// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

// Pipeline lifecycle queries: retention, webhook/schedule triggers, active-run checks, stuck-run reconcile.
internal sealed class PipelineLifecycleRepository(AppDbContext db, TimeProvider timeProvider)
{
    /// <summary>Delay of a pending approval that carries none and names no environment. The gate never
    /// raises one (a pipeline approval always has its own delay); a day, like an environment's default.</summary>
    private const int DefaultApprovalTimeoutMinutes = 1440;

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

        // A retry re-enters a workspace the agent may no longer have. `checkout: true` stopped cloning
        // (PipelineCommandBuilder's double-clone fix): only System:Prepare puts the repository in the
        // workspace, and it is Success, so it was never reset and the sources never came back.
        // Production run 2240 failed its retry twice on "cannot open
        // deploy/scripts/finalize-fast-release-transaction.sh" for exactly that reason, an agent
        // restart having taken its workspace with it. Replay the preparation together with the retry:
        // a shallow clone over a workspace that still holds one costs seconds, a retry that cannot see
        // the repository costs the whole run. FindReadyStages always lets System:Prepare through and
        // gates every user stage on its completion, so it runs first and the failed step follows it.
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

    /// <summary>F2: approvals still Pending whose environment's <c>ApprovalTimeoutMinutes</c> has
    /// elapsed since they were requested. Used by the reconcile sweep to auto-expire a request nobody
    /// answered instead of leaving the run parked forever.</summary>
    public async Task<List<int>> GetExpiredPendingApprovalIdsAsync(DateTime now, CancellationToken ct = default)
    {
        return await db.PipelineApprovals
            .AsNoTracking()
            .Where(a => a.Status == ApprovalStatus.Pending)
            // R-370: no inner join on the environment any more. A pipeline approval may have none, and
            // an inner join would leave it pending forever; it always carries its own delay instead.
            .Select(a => new
            {
                a.Id,
                a.RequestedAt,
                Timeout = a.TimeoutMinutes ?? (a.Environment != null ? a.Environment.ApprovalTimeoutMinutes : DefaultApprovalTimeoutMinutes)
            })
            .Where(x => x.RequestedAt.AddMinutes(x.Timeout) < now)
            .Select(x => x.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>PLAN-007 lot 7: approvals still waiting for a decision on a run that is still waiting for
    /// it, oldest first, limited to the pipelines the caller may read (null = all).</summary>
    public async Task<List<PendingApprovalDto>> GetPendingApprovalsAsync(
        List<int>? accessiblePipelineIds, CancellationToken ct = default)
    {
        return await db.PipelineApprovals
            .AsNoTracking()
            .Where(a => a.Status == ApprovalStatus.Pending
                && a.PipelineRun.Status == PipelineStatus.WaitingForApproval
                && (accessiblePipelineIds == null || accessiblePipelineIds.Contains(a.PipelineRun.PipelineId)))
            .OrderBy(a => a.RequestedAt)
            .Select(a => new PendingApprovalDto
            {
                ApprovalId = a.Id,
                PipelineRunId = a.PipelineRunId,
                PipelineId = a.PipelineRun.PipelineId,
                PipelineName = a.PipelineRun.Pipeline.Name,
                ProjectId = a.PipelineRun.Pipeline.ProjectId,
                StageName = a.StageName,
                Scope = a.Scope,
                EnvironmentName = a.Environment != null ? a.Environment.Name : null,
                RequestedAt = a.RequestedAt
            })
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CloseApprovalsOfEndedRunsAsync(int? runId, DateTime now, string reason, CancellationToken ct = default)
    {
        var query = db.PipelineApprovals
            .Where(a => a.Status == ApprovalStatus.Pending
                        && PipelineStatusFacts.Terminal.Contains(a.PipelineRun.Status));
        if (runId is { } id)
            query = query.Where(a => a.PipelineRunId == id);

        if (db.Database.IsRelational())
            return await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.Status, ApprovalStatus.Rejected)
                    .SetProperty(a => a.ResolvedAt, now)
                    .SetProperty(a => a.ResolvedByUserId, (int?)null)
                    .SetProperty(a => a.Comments, reason), ct)
                .ConfigureAwait(false);

        var pending = await query.ToListAsync(ct).ConfigureAwait(false);
        foreach (var approval in pending)
        {
            approval.Status = ApprovalStatus.Rejected;
            approval.ResolvedAt = now;
            approval.ResolvedByUserId = null;
            approval.Comments = reason;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return pending.Count;
    }

    public async Task<List<StrandedApprovalRun>> GetStrandedApprovalRunsAsync(CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AsNoTracking()
            .Where(run => run.Status == PipelineStatus.WaitingForApproval
                          && !db.PipelineApprovals.Any(a => a.PipelineRunId == run.Id
                                                           && a.Status == ApprovalStatus.Pending))
            .OrderBy(run => run.Id)
            .Select(run => new StrandedApprovalRun(
                run.Id,
                db.PipelineApprovals
                    .Where(a => a.PipelineRunId == run.Id)
                    .OrderByDescending(a => a.ResolvedAt)
                    .Select(a => (ApprovalStatus?)a.Status)
                    .FirstOrDefault()))
            // Bounded: this is an anomaly list, so a sweep that suddenly matches thousands of runs means
            // something systemic, and re-applying decisions to all of them at once is the wrong answer.
            // The cap keeps one sweep's blast radius small; the next sweep picks the rest up - which only
            // holds if the 50 are chosen deterministically. Without an order, PostgreSQL may hand back the
            // same arbitrary page every sweep and starve the rest forever, so take the oldest runs first:
            // they are the ones that have been parked the longest, and each sweep then makes real progress.
            .Take(50)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<PipelineApproval?> TryResolveApprovalAsync(
        int approvalId, ApprovalStatus decision, DateTime resolvedAt,
        int? resolvedByUserId, string? comments, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            var rows = await db.PipelineApprovals
                .Where(a => a.Id == approvalId && a.Status == ApprovalStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.Status, decision)
                    .SetProperty(a => a.ResolvedAt, resolvedAt)
                    .SetProperty(a => a.ResolvedByUserId, resolvedByUserId)
                    .SetProperty(a => a.Comments, comments), ct)
                .ConfigureAwait(false);
            if (rows != 1) return null;
            var tracked = db.ChangeTracker.Entries<PipelineApproval>().FirstOrDefault(e => e.Entity.Id == approvalId);
            if (tracked is not null) tracked.State = EntityState.Detached;
        }
        else
        {
            var approval = await db.PipelineApprovals
                .FirstOrDefaultAsync(a => a.Id == approvalId && a.Status == ApprovalStatus.Pending, ct)
                .ConfigureAwait(false);
            if (approval is null) return null;
            approval.Status = decision;
            approval.ResolvedAt = resolvedAt;
            approval.ResolvedByUserId = resolvedByUserId;
            approval.Comments = comments;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return await db.PipelineApprovals
            .AsNoTracking()
            .Include(a => a.Environment)
            .Include(a => a.ResolvedByUser)
            .FirstAsync(a => a.Id == approvalId, ct)
            .ConfigureAwait(false);
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

    public async Task<Dictionary<int, PipelineStatus>> GetRunStatusesByIdsAsync(IReadOnlyCollection<int> runIds, CancellationToken ct = default)
        => await db.PipelineRuns
            .AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Status })
            .ToDictionaryAsync(x => x.Id, x => x.Status, ct).ConfigureAwait(false);

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

    public async Task<int> DeleteRunsOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            return await db.PipelineRuns
                .Where(r => r.StartedAt < cutoff
                            && !r.Tasks.Any(task => task.IsDeferredCleanup
                                && (task.Status == TaskExecutionStatus.Pending
                                    || task.Status == TaskExecutionStatus.Assigned
                                    || task.Status == TaskExecutionStatus.Running)))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var old = await db.PipelineRuns
            .Where(r => r.StartedAt < cutoff
                        && !r.Tasks.Any(task => task.IsDeferredCleanup
                            && (task.Status == TaskExecutionStatus.Pending
                                || task.Status == TaskExecutionStatus.Assigned
                                || task.Status == TaskExecutionStatus.Running)))
            .ToListAsync(ct).ConfigureAwait(false);
        db.PipelineRuns.RemoveRange(old);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return old.Count;
    }

    public async Task<List<Pipeline>> GetWebhookTriggeredPipelinesWithProjectAsync(CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.TriggerType == PipelineTriggerType.Webhook)
            .Include(p => p.Project)
                .ThenInclude(pr => pr!.GitConnection)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Pipeline>> GetWebhookTriggeredPipelinesForProjectAsync(int projectId, CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId && p.TriggerType == PipelineTriggerType.Webhook)
            .Include(p => p.Project)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Pipeline>> GetScheduledPipelinesAsync(CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.TriggerType == PipelineTriggerType.Schedule)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> HasActiveRunAsync(int pipelineId, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AnyAsync(r => r.PipelineId == pipelineId &&
                           (r.Status == PipelineStatus.Running || r.Status == PipelineStatus.Pending || r.Status == PipelineStatus.WaitingForApproval), ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> LockPipelineForWebhookAsync(int pipelineId, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational())
            return await db.Pipelines.AnyAsync(pipeline => pipeline.Id == pipelineId, ct).ConfigureAwait(false);

        var rows = await db.Pipelines
            .Where(pipeline => pipeline.Id == pipelineId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(pipeline => pipeline.Name, pipeline => pipeline.Name),
                ct)
            .ConfigureAwait(false);
        return rows == 1;
    }

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

    public async Task<List<int>> GetActiveRunIdsAsync(int pipelineId, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .Where(run => run.PipelineId == pipelineId
                          && (run.Status == PipelineStatus.Running
                              || run.Status == PipelineStatus.Pending
                              || run.Status == PipelineStatus.WaitingForApproval))
            .OrderBy(run => run.Id)
            .Select(run => run.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<HashSet<int>> GetPipelineIdsWithActiveRunsAsync(CancellationToken ct = default)
    {
        var ids = await db.PipelineRuns
            .Where(r => r.Status == PipelineStatus.Running || r.Status == PipelineStatus.Pending || r.Status == PipelineStatus.WaitingForApproval)
            .Select(r => r.PipelineId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return ids.ToHashSet();
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
        var hydrated = runs.Select(PipelineRunDtoMapper.HydrateListWarnings).ToList();
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
        var hydrated = runs.Select(PipelineRunDtoMapper.HydrateListWarnings).ToList();
        var graded = await PipelineRunGradeAggregation.ApplyAsync(db, hydrated, ct).ConfigureAwait(false);
        return await PipelineRunRepositoryLinks.ApplyAsync(db, graded, ct).ConfigureAwait(false);
    }
}
