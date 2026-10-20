// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

public sealed record StepOutputProjection(string StageName, string StepName, string? OutputVariablesJson);
public sealed record PipelineRunRootReference(int RunId, int PipelineId, string PipelineName);
public sealed record TaskQueuePosition(int Position, int Depth);
public sealed record PipelineRunQueueReference(int StepId, int TaskId);

/// <summary>A run left in WaitingForApproval although no approval is pending on it any more, with the
/// decision that was last recorded (null when the run has no approval row at all).</summary>
public sealed record StrandedApprovalRun(int RunId, ApprovalStatus? LastDecision);

/// <summary>What <c>GET api/Pipelines/{id}/source</c> reads of a pipeline.</summary>
public sealed record PipelineSourceFields(int? ProjectId, string Name, string? SourceBranch, int? SourceRepositoryId);

/// <summary>
/// Facade over the pipeline data. It composes the narrow ports extracted for a real responsibility
/// (audit AUD-07), whose members it still exposes and delegates to the same queries; a caller that needs
/// only one responsibility depends on its port instead. <c>SaveChangesAsync</c> comes from
/// <see cref="IPipelineUnitOfWork"/>.
/// </summary>
public interface IPipelineRepository
    : IPipelineTemplateRepository, IPipelineRunnerTargetReader, IPipelineAutoTriggerRepository,
        IPipelineApprovalRepository, IPipelineQualityResultRepository, IPipelineArtifactRepository,
        IPipelineRunLookupReader, IPipelineEnvironmentReader, IPipelineReleaseNumberRepository,
        IPipelineRunRetentionRepository, IPipelineProjectOwnerReader
{
    Task<(List<Pipeline> Items, int TotalCount)> GetPipelinesPagedAsync(
        string? search, PipelineTriggerType? triggerType, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default);

    /// <summary>
    /// Projected variant of <see cref="GetPipelinesPagedAsync"/> that returns <see cref="PipelineDto"/>
    /// directly via a <c>.Select()</c> projection, avoiding loading full entity graphs.
    /// </summary>
    Task<(List<PipelineDto> Items, int TotalCount)> GetPipelinesPagedProjectedAsync(
        string? search, PipelineTriggerType? triggerType, int? environmentId, int? projectServerId, int? projectId,
        int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default);

    Task<List<PipelineDto>> GetPipelinesForDependencyGraphAsync(
        List<int>? accessibleIds = null, int? serverId = null, int? projectId = null,
        CancellationToken ct = default);
    Task<(List<PipelineDto> Items, List<PipelineDto> Identities, int TotalCount)> GetPipelineDependencyPageAsync(
        PipelinePaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default);

    Task<Pipeline?> GetPipelineWithRunsAsync(int id, CancellationToken ct = default);

    Task<Pipeline?> FindPipelineAsync(int id, CancellationToken ct = default);

    Task<Pipeline?> FindPipelineByNameAndProjectAsync(string name, int projectId, CancellationToken ct = default);

    /// <summary>Name and YAML of every pipeline of a project, the input of its unmet
    /// <c>requires:</c> check. Only the two columns are read.</summary>
    Task<List<(int Id, string Name, string Yaml)>> GetPipelineDefinitionsForProjectAsync(int projectId, CancellationToken ct = default);

    /// <summary>Returns timed steps from the last <paramref name="take"/> successful runs started
    /// before <paramref name="runId"/>; empty if the run belongs to another pipeline.</summary>
    Task<List<StepTimingRow>> GetRecentSuccessfulStepTimingsAsync(int pipelineId, int runId, int take, CancellationToken ct = default);

    Task AddPipelineAsync(Pipeline pipeline, CancellationToken ct = default);

    Task RemovePipelineAsync(Pipeline pipeline, CancellationToken ct = default);

    Task AddPipelineRunAsync(PipelineRun run, CancellationToken ct = default);
    Task<(PipelineRun Run, bool Created)> GetOrAddPipelineRunAsync(
        PipelineRun run, CancellationToken ct = default);

    /// <summary>Reserves the next per-pipeline build number (exposed as <c>BUILD_PIPELINE_RUNNUMBER</c>).
    /// Returns 0 when the pipeline no longer exists.</summary>
    Task<int> ReserveNextBuildNumberAsync(int pipelineId, CancellationToken ct = default);

    /// <summary>Tracks a step run on the change tracker. Caller must invoke <see cref="IPipelineUnitOfWork.SaveChangesAsync"/> to flush.</summary>
    void TrackPipelineStepRun(PipelineStepRun stepRun);

    Task<List<PipelineStepRun>> GetPendingStepRunsAsync(int runId, CancellationToken ct = default);

    /// <summary>All pipelines owned by an environment (used by the git-sync copy on project link).</summary>
    Task<List<Pipeline>> GetPipelinesByEnvironmentAsync(int environmentId, CancellationToken ct = default);

    /// <summary>Returns an environment's name and linked project for synchronizing pipeline
    /// definitions to the project's Git repository.</summary>
    Task<(string Name, int ProjectId)?> GetEnvironmentCopyTargetAsync(int environmentId, CancellationToken ct = default);

    /// <summary>True when at least one enrolled runner reports Docker available. Backs the only
    /// capability a <c>requires:</c> block may declare, so the declaration is verified rather than
    /// merely recorded. Enrolment, not being online: a runner that is briefly down still means the
    /// installation has the capability, and refusing a launch for that would be a wait, not a defect.</summary>
    Task<bool> HasRunnerWithDockerAsync(int? organizationId, CancellationToken ct = default);
    Task<Server?> FindServerByIdAsync(int serverId, CancellationToken ct = default);
    Task<int?> GetRunAffinityServerIdAsync(int runId, CancellationToken ct = default);
    Task<int?> GetStageProducerServerIdAsync(int runId, string stageName, CancellationToken ct = default);

    /// <summary>Organization owning a pipeline (via Project | Environment→Project | ProjectServer→Project).
    /// Null if unresolvable (legacy orphan pipeline).</summary>
    Task<int?> GetPipelineOrganizationIdAsync(int pipelineId, CancellationToken ct = default);

    Task<int?> GetPipelineOwnerOrganizationIdAsync(
        int? projectId, int? environmentId, int? projectServerId, CancellationToken ct = default);

    /// <summary>Organization of an AI runner profile; null when no such profile exists. Own-read for
    /// the check that a pipeline's assigned profile belongs to the pipeline's organization.</summary>
    Task<int?> GetAiRunnerProfileOrganizationIdAsync(int profileId, CancellationToken ct = default);

    /// <summary>Project owning a pipeline (direct ProjectId, or via Environment→ProjectId,
    /// or via ProjectServer→ProjectId). Null if unresolvable (legacy orphan).</summary>
    Task<int?> GetPipelineProjectIdAsync(Pipeline pipeline, CancellationToken ct = default);

    /// <summary>The release a deployment names, read inside the run's own project (a numeric
    /// selector is a release id, anything else an exact version, newest first - the selection the
    /// <c>restore-artifacts</c> step makes). Null when no such release exists.</summary>
    Task<DeploymentGateRelease?> FindDeploymentGateReleaseAsync(
        int projectId, string releaseSelector, CancellationToken ct = default);

    /// <summary>The release of <paramref name="version"/> in the project (newest first), as a
    /// <c>type: advance-branch</c> step reads it. Null when no such release exists.</summary>
    Task<BranchAdvanceRelease?> FindBranchAdvanceReleaseAsync(
        int projectId, string version, CancellationToken ct = default);

    /// <summary>Project a project-server row belongs to. Null when it does not exist. Authorizing a
    /// pipeline owned by a project server goes through this: there is no ResourceType.ProjectServer.</summary>
    Task<int?> GetProjectServerProjectIdAsync(int projectServerId, CancellationToken ct = default);
    // S-FEAT-16: project's default release version pattern, or null when unset.
    Task<string?> GetProjectReleasePatternAsync(int projectId, CancellationToken ct = default);
    /// <summary>The project's name, or null when it does not exist. Source of the slug a template's
    /// <c>${{ project.slug }}</c> library and vault names expand to (R3-67).</summary>
    Task<string?> GetProjectNameAsync(int projectId, CancellationToken ct = default);

    // F-EXEC-1: candidate-target enumeration for authorization. Unlike the FindOnlineServer*
    // methods (which pick one *online* server at scheduling time), these return EVERY server a
    // stage selector could resolve to regardless of status, so a pre-run Server.Admin check
    // cannot be bypassed via the online/offline race.
    Task<List<int>> FindServerIdsByAgentAsync(string? agent, CancellationToken ct = default);

    Task<List<int>> FindServerIdsInPoolAsync(string poolName, CancellationToken ct = default);

    Task<List<int>> FindServerIdsInEnvironmentAsync(string environmentName, int? projectId, CancellationToken ct = default);

    /// <summary>Tracks a server task on the change tracker. Caller must invoke <see cref="IPipelineUnitOfWork.SaveChangesAsync"/> to flush.</summary>
    void TrackTask(ServerTask task);
    /// <summary>Tracks an artifact a run consumed, flushed with the step's task.</summary>
    void TrackArtifactInput(PipelineRunArtifactInput input);
    void TrackDastExecutionLease(DastExecutionLease lease);

    Task<List<PipelineRun>> GetRunsAsync(int pipelineId, int count, CancellationToken ct = default);
    Task<(List<PipelineRunDto> Items, int TotalCount)> GetRunsPagedAsync(int pipelineId, int page, int pageSize, PipelineRunPaginationRequest? request = null, CancellationToken ct = default);
    Task<List<PipelineRunDto>> GetActiveRunsAsync(List<int>? accessiblePipelineIds = null, int? projectId = null, CancellationToken ct = default);
    Task<List<PipelineRunDto>> GetRecentRunsAsync(List<int>? accessiblePipelineIds = null, int? projectId = null, int? serverId = null, CancellationToken ct = default);

    /// <summary>The run with its pipeline, project and steps. no result rows; the test results are
    /// counted by <see cref="IPipelineQualityResultRepository.GetTestResultSummaryAsync"/>, the other results are read as figures by
    /// <see cref="GetRunResultSummariesAsync"/>.</summary>
    Task<PipelineRun?> GetRunDetailAsync(int runId, CancellationToken ct = default);

    /// <summary>The coverage and lint figures, metrics and artifacts of a run's detail, computed
    /// and projected by the database (the coverage per-file list is read on demand).</summary>
    Task<PipelineRunResultSummaries> GetRunResultSummariesAsync(PipelineRun run, CancellationToken ct = default);

    /// <summary>The project, name and source binding of a pipeline; null when unknown.</summary>
    Task<PipelineSourceFields?> GetPipelineSourceFieldsAsync(int id, CancellationToken ct = default);

    /// <summary>Completes an ungraded detail run's <c>GateGrade</c> from a linked run (trigger children,
    /// or the candidate release a deploy run restores). See <see cref="PipelineRunGradeAggregation"/>.
    /// A no-op (returns <paramref name="run"/> unchanged) when it already carries a grade.</summary>
    Task<PipelineRunDto> HydrateLinkedGradeAsync(PipelineRunDto run, CancellationToken ct = default);
    Task<Dictionary<int, TaskQueuePosition>> GetTaskQueuePositionsAsync(
        IReadOnlyCollection<int> taskIds, CancellationToken ct = default);
    Task<List<PipelineRunQueueReference>> GetRunQueueReferencesAsync(
        int runId, CancellationToken ct = default);

    Task<List<StepOutputProjection>> GetSuccessfulStepOutputsAsync(int runId, CancellationToken ct = default);

    Task<PipelineRun?> GetPipelineRunWithPipelineAsync(int runId, CancellationToken ct = default);

    /// <summary>Whether any step of these stages started at all, whatever its outcome.</summary>
    Task<bool> DidAnyStepStartInStagesAsync(int runId, IReadOnlyCollection<string> stageNames, CancellationToken ct = default);

    /// <summary>The pipeline's most recent run, tracked (a repeated refusal updates it).</summary>
    Task<PipelineRun?> GetLatestRunTrackedAsync(int pipelineId, CancellationToken ct = default);

    /// <summary>Resolves each supplied run to the root run and pipeline that started its trigger lineage.</summary>
    Task<Dictionary<int, PipelineRunRootReference>> GetRootRunReferencesAsync(
        IReadOnlyCollection<int> runIds, CancellationToken ct = default);

    Task<bool> AreAllStepsInStageCompletedAsync(int runId, string stageName, CancellationToken ct = default);

    /// <summary>Trigger-step orchestration: the (tracked) step runs that launched <paramref name="triggeredRunId"/>
    /// and are still waiting on it. Used by <c>PipelineRunCompletedTriggerHandler</c> to complete the parent step.</summary>
    Task<List<PipelineStepRun>> FindStepRunsByTriggeredRunIdAsync(int triggeredRunId, CancellationToken ct = default);
    Task<bool> TryResolveTriggeredStepAsync(
        int stepId,
        TaskExecutionStatus status,
        int exitCode,
        string? outputVariablesJson,
        string? failureCode,
        string? failureReason,
        DateTime completedAt,
        CancellationToken ct = default);

    /// <summary>Direct child runs launched by trigger steps of the supplied parent run.</summary>
    Task<List<int>> GetTriggeredChildRunIdsAsync(int parentRunId, CancellationToken ct = default);

    /// <summary>Finds the child run of a named pipeline which was triggered by a parent orchestration
    /// run. Used to resolve a verified build artifact across CI → QA → Deploy without trusting an id
    /// supplied by YAML.</summary>
    Task<int?> FindTriggeredRunIdByPipelineNameAsync(int parentRunId, string pipelineName, CancellationToken ct = default);

    /// <summary>Trigger steps still Running (and older than a grace period) that wait on a child run.
    /// The reconcile sweeper re-checks them because a trigger step has no ServerTask and so is invisible
    /// to the task-timeout sweep.</summary>
    Task<List<PipelineStepRun>> GetStuckRunningTriggerStepsAsync(DateTime startedBefore, CancellationToken ct = default);

    /// <summary>Active runs old enough to require a scheduler retry, with pending pipeline steps but no
    /// assigned/running step, in-flight server task, child trigger or approval that could wake them.</summary>
    Task<List<int>> GetStalledSchedulableRunIdsAsync(DateTime startedBefore, CancellationToken ct = default);

    /// <summary>Cancellation-requested runs with no active server task. The reconciliation sweep
    /// reapplies cancellation so orphan Running steps cannot prevent mandatory teardown.</summary>
    Task<List<int>> GetStalledCancellationRunIdsAsync(DateTime startedBefore, CancellationToken ct = default);

    /// <summary>Runs wedged in Running past <paramref name="startedBefore"/> with no in-flight task and no
    /// trigger step waiting on a child - i.e. runs that can no longer make progress. The reconcile sweep
    /// force-fails them.</summary>
    Task<List<int>> GetStuckRunningRunIdsAsync(DateTime startedBefore, CancellationToken ct = default);

    Task<bool> HasAnyStepFailedInStageAsync(int runId, string stageName, CancellationToken ct = default);

    /// <summary>True if any step (across any stage) has already failed for this run. Used to short-circuit
    /// the per-stage failure poll when evaluating conditional expressions.</summary>
    Task<bool> HasAnyFailedStepInRunAsync(int runId, CancellationToken ct = default);

    /// <summary>BACK-019: true when an artifact collection of this run failed, timed out, was cancelled
    /// or could not be dispatched. Such a run can never end Success.</summary>
    Task<bool> HasFailedArtifactCollectionAsync(int runId, CancellationToken ct = default);

    /// <summary>Failed steps with <c>continue_on_error</c> make the run <see
    /// cref="PipelineStatus.Partial"/> unless all such failures are advisory AI steps.</summary>
    Task<List<PipelineStepRun>> GetContinuableFailedStepsInRunAsync(int runId, CancellationToken ct = default);

    /// <summary>Checks that the named stages executed at least one step and all their steps
    /// succeeded, allowing rollback completion to be recognized.</summary>
    Task<bool> DidStagesAllSucceedAsync(int runId, IReadOnlyCollection<string> stageNames, CancellationToken ct = default);

    Task<bool> HasAnySucceededStepInRunAsync(int runId, CancellationToken ct = default);

    Task<List<string>> GetCompletedStageNamesAsync(int runId, CancellationToken ct = default);

    /// <summary>Stages whose steps have all reached a terminal status, including hard failures.
    /// Used to release <c>always()</c>/<c>failed()</c> handlers and the system cleanup without
    /// treating failed stages as successful dependencies for ordinary stages.</summary>
    Task<List<string>> GetTerminalStageNamesAsync(int runId, CancellationToken ct = default);

    Task<bool> IsRunStillRunningAsync(int runId, CancellationToken ct = default);

    Task<bool> HasAnyRunningStepInRunAsync(int runId, CancellationToken ct = default);

    Task<List<string>> GetActiveStageNamesAsync(int runId, CancellationToken ct = default);

    /// <summary>True while post-stage artifact collection is pending or executing.</summary>
    Task<bool> HasActiveArtifactCollectionAsync(int runId, CancellationToken ct = default);

    Task UpdatePipelineRunStatusAsync(int runId, PipelineStatus status, CancellationToken ct = default);
    Task<bool> TryTransitionPipelineRunStatusAsync(int runId, PipelineStatus expectedStatus, PipelineStatus newStatus, CancellationToken ct = default);

    /// <summary>Appends warnings to a run's <c>WarningsJson</c> via a tracked entity and persists immediately.
    /// The no-server failure path must use this rather than mutating the result of
    /// <see cref="GetPipelineRunWithPipelineAsync"/> (which is <c>AsNoTracking</c> - mutations there are
    /// silently dropped, leaving a failed run with no visible error message).</summary>
    Task AppendRunWarningsAsync(int runId, IReadOnlyCollection<string> warnings, CancellationToken ct = default);

    /// <summary>Records why the last scheduling pass dispatched nothing, or clears it with <c>null</c>
    /// once something moved. Writes nothing when the reason is unchanged: the scheduler re-passes over
    /// a waiting run continuously, and an UPDATE per pass would both cost a write and reset the
    /// "waiting since" clock the reason exists to provide.</summary>
    /// <returns><c>true</c> when the stored reason actually changed.</returns>
    Task<bool> SetRunWaitingReasonAsync(int runId, string? reason, CancellationToken ct = default);

    /// <summary>Cancels every non-terminal step and server task still attached to the run.</summary>
    Task CancelActiveStepRunsAndTasksAsync(int runId, CancellationToken ct = default);

    Task CancelPendingStepRunsAsync(int runId, CancellationToken ct = default);
    /// <summary>Marks the run as cancellation-requested; the first request's author and reason are
    /// kept, a repeated request does not overwrite them.</summary>
    Task RequestPipelineRunCancellationAsync(int runId, PipelineRunCancellation cancellation, CancellationToken ct = default);
    Task CancelPendingStepRunsExceptStagesAsync(
        int runId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct = default);
    Task CancelOrphanedRunningStepRunsExceptStagesAsync(
        int runId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct = default);

    /// <summary>Resets every <c>Failed</c> step run of a run back to <c>Pending</c> and re-opens the
    /// run (Status → Running, CompletedAt → null) so the scheduler reruns only the failed steps.
    /// Returns the number of steps reset (0 if none).</summary>
    Task<int> ResetFailedStepRunsAsync(int runId, CancellationToken ct = default);

    Task<string?> FindPublishedReleaseVersionByRunIdAsync(int pipelineRunId, CancellationToken ct = default);

    /// <summary>Project owning the run's pipeline (directly, or through its environment or project
    /// server). Null when the run does not exist or its pipeline belongs to no project.</summary>
    Task<int?> GetRunProjectIdAsync(int runId, CancellationToken ct = default);
    Task<Data.Entities.Environment?> FindEnvironmentByNameForProjectAsync(string name, int projectId, CancellationToken ct = default);

    Task<List<PipelineStepRun>> GetFailedStepRunsInStageAsync(int runId, string stageName, CancellationToken ct = default);

    Task<bool> IsStepRetryEligibleAsync(int pipelineRunId, int stepRunId, CancellationToken ct = default);

    /// <summary>Every run still in flight, any pipeline. Feeds the agents' workspace reaper.</summary>
    Task<List<int>> GetAllActiveRunIdsAsync(CancellationToken ct = default);

    /// <summary>Checks whether the server was assigned to a step of the run, authorizing agent
    /// artifact and test-result submissions.</summary>
    Task<bool> IsServerAssignedToRunAsync(int runId, int serverId, CancellationToken ct = default);

    /// <summary>The stage whose step the server is running in this run, or null when it runs none or
    /// steps of several stages at once (the stage is then not decidable from the run alone).</summary>
    Task<string?> FindRunningStageNameAsync(int runId, int serverId, CancellationToken ct = default);
}
