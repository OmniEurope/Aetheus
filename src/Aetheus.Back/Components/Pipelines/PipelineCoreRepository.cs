// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

// Core pipeline data access (paged lists, CRUD, run/step/template/artifact/approval queries).
// Extracted from the former PipelineRepository partial into a focused collaborator; the
// PipelineRepository facade composes this plus the resolver/coverage/lifecycle collaborators.
internal sealed class PipelineCoreRepository(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger logger,
    IPipelineTaskLifecycle taskLifecycle)
{
    private readonly PipelineTriggerTransitionRepository _triggerTransitions = new(db);
    private readonly PipelineArtifactRepository _artifacts = new(db);
    private readonly PipelineCancellationRepository _cancellation =
        new(db, timeProvider, taskLifecycle);
    private readonly PipelineRunStatusRepository _runStatusRepository = new(db, logger);
    private readonly PipelineBuildNumberRepository _buildNumbers = new(db);
    private readonly PipelineRunNarrativeRepository _narrative = new(db, timeProvider);

    public async Task<(List<Pipeline> Items, int TotalCount)> GetPipelinesPagedAsync(
        string? search, PipelineTriggerType? triggerType, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = FilterPipelines(search, triggerType, accessibleIds);
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await InProgressFirst(query
            .Include(p => p.Project)
            .Include(p => p.Environment)
            .Include(p => p.ProjectServer)
            .Include(p => p.Runs.OrderByDescending(r => r.StartedAt).Take(5)))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    /// <summary>
    /// Recette R2-093: a pipeline with a run in progress or awaiting an approval leads the list, the most
    /// recently started first, then the others by name as before. Shared by the two paged lists.
    /// </summary>
    private static IOrderedQueryable<Pipeline> InProgressFirst(IQueryable<Pipeline> query) => query
        .OrderByDescending(p => p.Runs.Any(r => !PipelineStatusFacts.Terminal.Contains(r.Status)))
        .ThenByDescending(p => p.Runs
            .Where(r => !PipelineStatusFacts.Terminal.Contains(r.Status))
            .Max(r => (DateTime?)r.StartedAt))
        .ThenBy(p => p.Name);

    // Shared filters of the two paged lists (entity vs. projected), so they cannot drift apart.
    private IQueryable<Pipeline> FilterPipelines(
        string? search, PipelineTriggerType? triggerType, List<int>? accessibleIds)
    {
        var query = db.Pipelines.AsNoTracking().AsQueryable();

        if (accessibleIds is not null)
            query = query.Where(p => accessibleIds.Contains(p.Id));

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search));

        if (triggerType.HasValue)
            query = query.Where(p => p.TriggerType == triggerType.Value);

        return query;
    }

    public async Task<(List<PipelineDto> Items, int TotalCount)> GetPipelinesPagedProjectedAsync(
        string? search, PipelineTriggerType? triggerType, int? environmentId, int? projectServerId, int? projectId,
        int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = FilterPipelines(search, triggerType, accessibleIds);

        // X4F9: project scope filters server-side on ProjectId (the project-detail list used to load the
        // whole ProjectDetailDto just for .Pipelines). Independent of the env/server scope filters below.
        if (projectId.HasValue)
            query = query.Where(p => p.ProjectId == projectId.Value);

        if (environmentId.HasValue)
            query = query.Where(p => p.EnvironmentId == environmentId.Value);
        else if (projectServerId.HasValue)
            query = query.Where(p => p.ProjectServerId == projectServerId.Value);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await InProgressFirst(query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PipelineDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                YamlDefinition = p.YamlDefinition,
                TriggerType = p.TriggerType,
                ProjectId = p.ProjectId,
                ProjectName = p.Project != null ? p.Project.Name : null,
                SourceBranch = p.SourceBranch,
                EnvironmentId = p.EnvironmentId,
                EnvironmentName = p.Environment != null ? p.Environment.Name : null,
                ProjectServerId = p.ProjectServerId,
                ProjectServerName = p.ProjectServer != null ? p.ProjectServer.DisplayName : null,
                LastRunStatus = p.Runs.OrderByDescending(r => r.StartedAt).Select(r => (PipelineStatus?)r.Status).FirstOrDefault(),
                LastRunAt = p.Runs.OrderByDescending(r => r.StartedAt).Select(r => (DateTime?)r.StartedAt).FirstOrDefault(),
                RecentRuns = p.Runs.OrderByDescending(r => r.StartedAt).Take(5).Select(r => new PipelineRunSummaryDto
                {
                    Id = r.Id,
                    Status = r.Status,
                    StartedAt = r.StartedAt,
                    CompletedAt = r.CompletedAt,
                    ServerName = r.StepRuns.Where(s => !s.IsSystem && s.ServerId != null).OrderBy(s => s.Order).Select(s => s.Server!.Name).FirstOrDefault(),
                    ServerOs = r.StepRuns.Where(s => !s.IsSystem && s.ServerId != null).OrderBy(s => s.Order).Select(s => s.Server!.OsDescription).FirstOrDefault()
                }).ToList(),
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            })
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<Pipeline?> GetPipelineWithRunsAsync(int id, CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Include(p => p.Project)
            .Include(p => p.Environment)
            .Include(p => p.ProjectServer)
            .Include(p => p.Runs.OrderByDescending(r => r.StartedAt).Take(5))
                .ThenInclude(r => r.StepRuns.Where(s => !s.IsSystem && s.ServerId != null).OrderBy(s => s.Order).Take(1))
                    .ThenInclude(s => s.Server)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<Pipeline?> FindPipelineAsync(int id, CancellationToken ct = default)
    {
        return await db.Pipelines
            .Where(p => p.Id == id)
            .Include(p => p.Project)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }


    public async Task AddPipelineAsync(Pipeline pipeline, CancellationToken ct = default)
    {
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemovePipelineAsync(Pipeline pipeline, CancellationToken ct = default)
    {
        db.Pipelines.Remove(pipeline);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddPipelineRunAsync(PipelineRun run, CancellationToken ct = default)
    {
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<int> ReserveNextBuildNumberAsync(int pipelineId, CancellationToken ct = default)
        => _buildNumbers.ReserveNextAsync(pipelineId, ct);

    public async Task<(PipelineRun Run, bool Created)> GetOrAddPipelineRunAsync(
        PipelineRun run, CancellationToken ct = default)
        => await PipelineRunIdempotencyWriter.GetOrAddAsync(db, run, ct).ConfigureAwait(false);

    public void TrackPipelineStepRun(PipelineStepRun stepRun)
    {
        db.PipelineStepRuns.Add(stepRun);
    }

    public async Task<List<PipelineStepRun>> GetPendingStepRunsAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId && s.Status == TaskExecutionStatus.Pending)
            .OrderBy(s => s.Order)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // Server resolution is delegated to PipelineServerResolver.

    public void TrackTask(ServerTask task)
    {
        db.Tasks.Add(task);
    }

    public void TrackArtifactInput(PipelineRunArtifactInput input) => db.PipelineRunArtifactInputs.Add(input);

    public void TrackDastExecutionLease(DastExecutionLease lease)
    {
        db.DastExecutionLeases.Add(lease);
    }

    public async Task<List<PipelineRun>> GetRunsAsync(int pipelineId, int count, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AsNoTracking()
            .Where(r => r.PipelineId == pipelineId)
            .Include(r => r.Pipeline)
            .Include(r => r.StepRuns)
            .OrderByDescending(r => r.StartedAt)
            .Take(count)
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // Server-side projection (PipelineRunDtoMapper.RunListProjection): the run LIST only renders
    // scalar run fields + step summary chips, so the heavy per-run payload (YamlSnapshot,
    // resolved-variables JSON, step commands, output variables, artifacts) never leaves the
    // database - and output variables never bypass the secret masking GetRunAsync applies.
    public async Task<(List<PipelineRunDto> Items, int TotalCount)> GetRunsPagedAsync(
        int pipelineId, int page, int pageSize, PipelineRunPaginationRequest? request = null, CancellationToken ct = default)
    {
        var query = PipelineRunQuery.ApplyFilters(
            db.PipelineRuns.AsNoTracking().Where(r => r.PipelineId == pipelineId), request);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await PipelineRunQuery.ApplySort(query, request, timeProvider.GetUtcNow().UtcDateTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(PipelineRunDtoMapper.RunListProjection)
            .ToListAsync(ct).ConfigureAwait(false);
        items = items.Select(PipelineRunDtoMapper.HydrateListWarnings).ToList();
        items = await PipelineRunGradeAggregation.ApplyAsync(db, items, ct).ConfigureAwait(false);
        items = await PipelineRunRepositoryLinks.ApplyAsync(db, items, ct).ConfigureAwait(false);
        return (items, total);
    }

    /// <summary>Recette R-483: the four columns the source of a pipeline needs, not the pipeline and its runs.</summary>
    public async Task<PipelineSourceFields?> GetPipelineSourceFieldsAsync(int id, CancellationToken ct = default) =>
        await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new PipelineSourceFields(p.ProjectId, p.Name, p.SourceBranch, p.SourceRepositoryId))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<List<StepOutputProjection>> GetSuccessfulStepOutputsAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns
            .AsNoTracking()
            .Where(s => s.PipelineRunId == runId && s.Status == TaskExecutionStatus.Success && s.OutputVariablesJson != null)
            .OrderBy(s => s.Order)
            .Select(s => new StepOutputProjection(s.StageName, s.StepName, s.OutputVariablesJson))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<PipelineRun?> GetPipelineRunWithPipelineAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AsNoTracking()
            .Where(r => r.Id == runId)
            .Include(r => r.Pipeline)
                .ThenInclude(p => p!.Project)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> FindTriggeredRunIdByPipelineNameAsync(int parentRunId, string pipelineName, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns.AsNoTracking()
            .Where(s => s.PipelineRunId == parentRunId && s.TriggeredRunId != null)
            .Join(db.PipelineRuns.AsNoTracking(), s => s.TriggeredRunId, r => r.Id, (_, r) => r)
            .Where(r => r.Pipeline.Name == pipelineName)
            .OrderByDescending(r => r.Id)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> GetPipelineIdForRunAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => (int?)r.PipelineId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> IsServerAssignedToRunAsync(int runId, int serverId, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns.AsNoTracking()
            .AnyAsync(s => s.PipelineRunId == runId && s.ServerId == serverId, ct).ConfigureAwait(false);
    }

    public async Task<string?> FindRunningStageNameAsync(int runId, int serverId, CancellationToken ct = default)
    {
        var stages = await db.PipelineStepRuns.AsNoTracking()
            .Where(s => s.PipelineRunId == runId && s.ServerId == serverId && s.Status == TaskExecutionStatus.Running)
            // Ordered before Take (recette R2-017): EF warns on a row limit without an order, and the
            // answer only needs to know whether exactly one distinct stage is running.
            .Select(s => s.StageName).Distinct().OrderBy(name => name).Take(2).ToListAsync(ct).ConfigureAwait(false);
        return stages.Count == 1 ? stages[0] : null;
    }

    public async Task<int?> GetPipelineIdForApprovalAsync(int approvalId, CancellationToken ct = default)
    {
        return await db.PipelineApprovals.AsNoTracking()
            .Where(a => a.Id == approvalId)
            .Select(a => (int?)a.PipelineRun.PipelineId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> GetPipelineIdForArtifactAsync(int artifactId, CancellationToken ct = default)
    {
        return await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.Id == artifactId)
            .Select(a => (int?)a.PipelineRun.PipelineId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> AreAllStepsInStageCompletedAsync(int runId, string stageName, CancellationToken ct = default)
    {
        return !await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId && s.StageName == stageName)
            .AnyAsync(s => s.Status != TaskExecutionStatus.Success && s.Status != TaskExecutionStatus.Failed && s.Status != TaskExecutionStatus.Timeout && s.Status != TaskExecutionStatus.Cancelled, ct)
            .ConfigureAwait(false);
    }
    // The persisted transition is atomic and id-based; callers only need the step context.
    public async Task<List<PipelineStepRun>> FindStepRunsByTriggeredRunIdAsync(int triggeredRunId, CancellationToken ct = default)
        => await db.PipelineStepRuns
            .AsNoTracking()
            .Where(s => s.TriggeredRunId == triggeredRunId)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> TryResolveTriggeredStepAsync(
        int stepId, TaskExecutionStatus status, int exitCode,
        string? outputVariablesJson, string? failureCode, string? failureReason,
        DateTime completedAt,
        CancellationToken ct = default)
        => await _triggerTransitions.TryResolveAsync(
            stepId, status, exitCode, outputVariablesJson, failureCode, failureReason, completedAt, ct).ConfigureAwait(false);

    public async Task<List<int>> GetTriggeredChildRunIdsAsync(int parentRunId, CancellationToken ct = default)
        => await db.PipelineStepRuns.AsNoTracking()
            .Where(s => s.PipelineRunId == parentRunId && s.TriggeredRunId != null)
            .Select(s => s.TriggeredRunId!.Value)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> HasAnyStepFailedInStageAsync(int runId, string stageName, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId && s.StageName == stageName)
            .AnyAsync(s => s.Status == TaskExecutionStatus.Failed || s.Status == TaskExecutionStatus.Timeout, ct)
            .ConfigureAwait(false);
    }

    public Task<bool> HasFailedArtifactCollectionAsync(int runId, CancellationToken ct = default)
        => _runStatusRepository.HasFailedArtifactCollectionAsync(runId, ct);

    public async Task<bool> HasAnyFailedStepInRunAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId && (s.Status == TaskExecutionStatus.Failed || s.Status == TaskExecutionStatus.Timeout) && !s.ContinueOnError)
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<PipelineStepRun>> GetContinuableFailedStepsInRunAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns.AsNoTracking()
            .Where(s => s.PipelineRunId == runId
                && (s.Status == TaskExecutionStatus.Failed || s.Status == TaskExecutionStatus.Timeout)
                && s.ContinueOnError)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> HasAnySucceededStepInRunAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId && !s.IsSystem && s.Status == TaskExecutionStatus.Success)
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }

    // Completed = every step terminal AND each one passed, was cancelled or may continue on error.
    public Task<List<string>> GetCompletedStageNamesAsync(int runId, CancellationToken ct = default) =>
        GetStageNamesAsync(runId, requirePassedOrContinuable: true, ct);

    public Task<List<string>> GetTerminalStageNamesAsync(int runId, CancellationToken ct = default) =>
        GetStageNamesAsync(runId, requirePassedOrContinuable: false, ct);

    private async Task<List<string>> GetStageNamesAsync(
        int runId, bool requirePassedOrContinuable, CancellationToken ct)
    {
        var allStageSteps = await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId)
            .GroupBy(s => s.StageName)
            .Select(g => new
            {
                StageName = g.Key,
                AllTerminal = g.All(s => s.Status == TaskExecutionStatus.Success
                    || s.Status == TaskExecutionStatus.Failed
                    || s.Status == TaskExecutionStatus.Timeout
                    || s.Status == TaskExecutionStatus.Cancelled),
                AllPassedOrContinuable = g.All(s => s.Status == TaskExecutionStatus.Success
                    || s.Status == TaskExecutionStatus.Cancelled
                    || ((s.Status == TaskExecutionStatus.Failed || s.Status == TaskExecutionStatus.Timeout) && s.ContinueOnError))
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return allStageSteps
            .Where(s => s.AllTerminal && (!requirePassedOrContinuable || s.AllPassedOrContinuable))
            .Select(s => s.StageName)
            .ToList();
    }

    public Task<bool> HasActiveArtifactCollectionAsync(
        int runId,
        CancellationToken ct = default) =>
        db.Tasks.AnyAsync(
            task => task.PipelineRunId == runId
                && task.Operation == OperationKind.PipelineCollectArtifacts
                && (task.Status == TaskExecutionStatus.Pending
                    || task.Status == TaskExecutionStatus.Assigned
                    || task.Status == TaskExecutionStatus.Running),
            ct);

    public async Task<bool> IsRunStillRunningAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AsNoTracking()
            .AnyAsync(r => r.Id == runId && r.Status == PipelineStatus.Running, ct)
            .ConfigureAwait(false);
    }

    public async Task UpdatePipelineRunStatusAsync(int runId, PipelineStatus status, CancellationToken ct = default)
    {
        status = await _runStatusRepository.ResolveTerminalStatusAsync(runId, status, ct)
            .ConfigureAwait(false);

        // Atomic update: only transition from Running to a terminal status.
        // Prevents concurrent AdvanceStageAsync calls from racing (first writer wins).
        var now = timeProvider.GetUtcNow().UtcDateTime;
        int rows;
        if (db.Database.IsRelational())
        {
            rows = await db.PipelineRuns
                .Where(r => r.Id == runId && r.Status == PipelineStatus.Running)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.CompletedAt, status.IsTerminal() ? now : (DateTime?)null), ct)
                .ConfigureAwait(false);

            // ExecuteUpdateAsync bypasses the change tracker. Evict any tracked PipelineRun
            // entity so a subsequent SaveChangesAsync in the same scope doesn't overwrite
            // the DB with a stale Status value.
            var tracked = db.ChangeTracker.Entries<PipelineRun>()
                .FirstOrDefault(e => e.Entity.Id == runId);
            if (tracked is not null)
                tracked.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
        else
        {
            // InMemory (unit tests)
            var run = await db.PipelineRuns.FirstOrDefaultAsync(r => r.Id == runId && r.Status == PipelineStatus.Running, ct).ConfigureAwait(false);
            if (run is not null)
            {
                run.Status = status;
                if (status.IsTerminal())
                    run.CompletedAt = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                rows = 1;
            }
            else rows = 0;
        }
        if (rows == 0)
            logger.LogDebug("Run {RunId} status update to {Status} skipped (no longer Running)", runId, status);
    }

    public Task AppendRunWarningsAsync(int runId, IReadOnlyCollection<string> warnings, CancellationToken ct = default)
        => _narrative.AppendWarningsAsync(runId, warnings, ct);

    public Task<bool> SetRunWaitingReasonAsync(int runId, string? reason, CancellationToken ct = default)
        => _narrative.SetWaitingReasonAsync(runId, reason, ct);

    // Pool, environment and organization resolution is delegated to PipelineServerResolver.

    public Task CancelActiveStepRunsAndTasksAsync(int runId, CancellationToken ct = default)
        => _cancellation.CancelActiveStepRunsAndTasksAsync(runId, ct);

    public Task CancelPendingStepRunsAsync(int runId, CancellationToken ct = default)
        => _cancellation.CancelPendingStepRunsAsync(runId, ct);

    public Task RequestPipelineRunCancellationAsync(int runId, PipelineRunCancellation cancellation, CancellationToken ct = default)
        => _cancellation.RequestPipelineRunCancellationAsync(runId, cancellation, ct);

    public Task CancelPendingStepRunsExceptStagesAsync(
        int runId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct = default)
        => _cancellation.CancelPendingStepRunsExceptStagesAsync(runId, preservedStages, ct);

    public Task CancelOrphanedRunningStepRunsExceptStagesAsync(
        int runId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct = default)
        => _cancellation.CancelOrphanedRunningStepRunsExceptStagesAsync(runId, preservedStages, ct);

    public Task<List<PipelineArtifact>> GetArtifactsAsync(int runId, CancellationToken ct = default)
        => _artifacts.GetArtifactsAsync(runId, ct);

    public Task AddArtifactAsync(PipelineArtifact artifact, CancellationToken ct = default)
        => _artifacts.AddArtifactAsync(artifact, ct);

    public async Task<List<PipelineApproval>> GetApprovalsAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineApprovals
            .AsNoTracking()
            .Where(a => a.PipelineRunId == runId)
            .Include(a => a.Environment)
            .Include(a => a.ResolvedByUser)
            .OrderBy(a => a.RequestedAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<PipelineApproval?> FindApprovalAsync(int approvalId, CancellationToken ct = default)
    {
        return await db.PipelineApprovals
            .Include(a => a.Environment)
            .Include(a => a.ResolvedByUser)
            .FirstOrDefaultAsync(a => a.Id == approvalId, ct).ConfigureAwait(false);
    }

    public async Task AddApprovalAsync(PipelineApproval approval, CancellationToken ct = default)
    {
        db.PipelineApprovals.Add(approval);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Data.Entities.Environment?> FindEnvironmentByNameAsync(
        string name, int? projectId, CancellationToken ct = default)
    {
        // AsSplitQuery: two collection Includes on one query is a cartesian product (servers x checks).
        // AsNoTracking: every caller reads this environment to decide something, none of them mutates
        // it, and tracking an environment with its whole server list is the expensive half of the call.
        // BACK-018: a name is unique per project only, so the run's project's own environment comes
        // first, then a shared one (no project), never another project's; the order makes it deterministic.
        return await db.Environments
            .AsNoTracking()
            .Where(e => e.Name == name && (e.ProjectId == null || e.ProjectId == projectId))
            .Include(e => e.Servers).ThenInclude(es => es.Server)
            .Include(e => e.Checks)
            .AsSplitQuery()
            .OrderBy(e => e.ProjectId == null)
            .ThenBy(e => e.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public Task<int?> GetRunProjectIdAsync(int runId, CancellationToken ct = default) =>
        db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => run.Pipeline.ProjectId
                ?? run.Pipeline.Environment!.ProjectId
                ?? (run.Pipeline.ProjectServer == null ? null : (int?)run.Pipeline.ProjectServer.ProjectId))
            .FirstOrDefaultAsync(ct);

    public Task<Data.Entities.Environment?> FindEnvironmentByNameForProjectAsync(
        string name,
        int projectId,
        CancellationToken ct = default) =>
        db.Environments.AsNoTracking()
            .FirstOrDefaultAsync(environment => environment.Name == name && environment.ProjectId == projectId, ct);

    public async Task<List<TestResult>> GetTestResultsAsync(int runId, CancellationToken ct = default)
    {
        return await db.TestResults
            .AsNoTracking()
            .Where(t => t.PipelineRunId == runId)
            .OrderBy(t => t.TestSuite)
            .ThenBy(t => t.TestName)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddTestResultsAsync(IEnumerable<TestResult> results, CancellationToken ct = default)
    {
        db.TestResults.AddRange(results);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<PipelineStepRun>> GetFailedStepRunsInStageAsync(int runId, string stageName, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId && s.StageName == stageName && (s.Status == TaskExecutionStatus.Failed || s.Status == TaskExecutionStatus.Timeout))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<EnvironmentCheck>> GetEnvironmentChecksAsync(int environmentId, CancellationToken ct = default)
    {
        return await db.EnvironmentChecks
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // Pipeline lifecycle queries (retention, webhook/schedule, active-run) live in
    // PipelineRepository.Lifecycle.cs to stay within the 600-line file budget.

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
