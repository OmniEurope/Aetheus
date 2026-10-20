// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineRunService
{
    /// <summary>
    /// Advances the run past a stage whose steps have all settled.
    ///
    /// Declared here rather than on a port outside both modules. That port existed to keep Tasks from
    /// depending on Pipelines, but resolving it landed on this very service, so the dependency was
    /// real and the two modules stayed in one cycle. Tasks now raises
    /// <c>PipelineStepTaskCompletedEvent</c> and this side reacts, which is the direction the module
    /// layering wants: "a step finished" is a notification from below.
    /// </summary>
    Task AdvanceStageAsync(int pipelineRunId, string completedStageName, CancellationToken ct = default);

    /// <summary>Resumes a run after its artifact collection task settled, which carries no step.</summary>
    Task ContinueAfterArtifactCollectionAsync(
        int pipelineRunId, TaskExecutionStatus collectionStatus, CancellationToken ct = default);

    /// <summary>Resolves the exact branch, commit, YAML and target servers that a run would execute.
    /// Callers must authorize <see cref="PipelineRunPreparation.TargetServerIds"/> before launching.</summary>
    Task<PipelineRunPreparation?> PrepareRunAsync(int pipelineId, string? sourceBranch = null,
        CancellationToken ct = default);

    /// <summary>The same resolution pinned to <paramref name="commitHash"/> instead of the branch head:
    /// the definition is read and the workspace checked out at that commit (an archived release's
    /// relaunch, decision D2).</summary>
    Task<PipelineRunPreparation?> PrepareRunAtCommitAsync(int pipelineId, string? sourceBranch, string commitHash,
        CancellationToken ct = default);

    /// <summary>Applies queue-time parameters to every YAML field and recomputes the exact target
    /// servers. Callers authorize the returned preparation before launching it.</summary>
    Task<PipelineRunPreparation> ResolveRunParametersAsync(
        PipelineRunPreparation preparation,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct = default);

    /// <summary>Launches an already-resolved snapshot. No Git ref is read again.</summary>
    Task<PipelineRunDto?> TriggerPreparedRunAsync(PipelineRunPreparation preparation,
        Dictionary<string, string>? additionalVariables = null,
        Dictionary<string, string>? parameters = null, CancellationToken ct = default,
        string? idempotencyKey = null);

    Task<PipelineRunDto?> TriggerRunAsync(int id, Dictionary<string, string>? additionalVariables = null,
        Dictionary<string, string>? parameters = null, CancellationToken ct = default);

    /// <summary>P: the queue-time parameters declared by the pipeline's authoritative (git-first) YAML,
    /// for rendering the run dialog. Empty list when the pipeline declares no <c>parameters:</c>.</summary>
    Task<List<PipelineRunParameterDto>> GetRunParametersAsync(int pipelineId, string? sourceBranch = null, CancellationToken ct = default);
    Task<PaginatedResult<PipelineRunDto>> GetRunsAsync(int pipelineId, PipelineRunPaginationRequest request, CancellationToken ct = default);
    Task<List<PipelineRunDto>> GetActiveRunsAsync(List<int>? accessiblePipelineIds = null, int? projectId = null, CancellationToken ct = default);
    Task<List<PipelineRunDto>> GetRecentRunsAsync(List<int>? accessiblePipelineIds = null, int? projectId = null, int? serverId = null, CancellationToken ct = default);
    Task<PipelineRunDto?> GetRunAsync(int runId, CancellationToken ct = default);

    /// <summary>
    /// Opaque workspace slots of the runs still in flight. An agent reaping stale run workspaces
    /// keeps these and deletes the rest, so the answer must never omit a live run.
    /// </summary>
    Task<List<string>> GetActiveWorkspaceSlotsAsync(CancellationToken ct = default);
    Task<PipelineRunQueueStateDto> GetRunQueueStateAsync(int runId, CancellationToken ct = default);
    Task<DryRunResultDto?> DryRunAsync(int pipelineId, Dictionary<string, string>? additionalVars = null, CancellationToken ct = default);

    /// <summary>Resolves each stage's target server without launching, so the caller can warn
    /// when no online agent matches. Returns null if the pipeline or its YAML is invalid.</summary>
    Task<PipelinePreflightDto?> PreflightAsync(int pipelineId, Dictionary<string, string>? additionalVars = null, CancellationToken ct = default);
    Task<PipelinePreflightDto?> PreflightAsync(PipelineRunPreparation preparation,
        Dictionary<string, string>? additionalVars = null, CancellationToken ct = default);

    /// <summary>
    /// F-EXEC-1: every server id the pipeline's stages could resolve to (pool &gt; environment &gt;
    /// agent precedence, ANY status). The caller MUST verify the triggering principal holds
    /// <see cref="Aetheus.Shared.Components.Auth.Permission.Admin"/> on each before launching - pipeline
    /// steps are free-form shell (= RCE), so this mirrors the F-15 gate on <c>POST /api/tasks</c>.
    /// Returns an empty set when the pipeline/YAML is invalid or matches no server (the run cannot
    /// execute, so there is nothing to authorize).
    /// </summary>
    Task<IReadOnlyCollection<int>> ResolveCandidateTargetServerIdsAsync(int pipelineId, CancellationToken ct = default);

    /// <summary>
    /// F-EXEC-1b: non-interactive trigger (webhook / scheduler). There is no
    /// <see cref="System.Security.Claims.ClaimsPrincipal"/>, so the run is authorized against
    /// the pipeline OWNER (<c>Pipeline.CreatedByUsername</c>): the pipeline must be owned and
    /// the owner must hold <see cref="Aetheus.Shared.Components.Auth.Permission.Admin"/> on every
    /// resolvable target server. Fail-closed - returns <c>null</c> without launching when the
    /// pipeline is unowned or the owner lacks the required permission (the breach is logged and
    /// audited). Mirrors the interactive F-EXEC-1 gate for the no-principal paths.
    /// </summary>
    Task<PipelineRunDto?> TriggerAutomatedRunAsync(int pipelineId, string triggerSource, Dictionary<string, string>? additionalVariables = null, CancellationToken ct = default);

    /// <summary> Launches a run prepared by <see cref="PrepareAutomatedRunAsync"/> for an automated
    /// trigger. A refusal is recorded as a failed run carrying the reason and told to the project's
    /// subscribers, then thrown again. </summary>
    Task<PipelineRunDto?> TriggerPreparedAutomatedRunAsync(
        PipelineRunPreparation preparation, string triggerSource,
        Dictionary<string, string>? additionalVariables = null, CancellationToken ct = default);

    /// <summary>
    /// Records a refused automated launch as a failed run carrying the reason. For a caller whose own
    /// transaction rolled back the record the launcher made; does nothing for an unknown pipeline.
    /// </summary>
    Task RecordRefusedAutomatedLaunchAsync(
        int pipelineId, string triggerSource, string reason,
        IReadOnlyDictionary<string, string>? additionalVariables = null, CancellationToken ct = default);

    /// <summary>Resolves and authorizes an automated run without persisting it. Callers that
    /// coordinate replacement must complete this phase before cancelling an existing run. A refused
    /// preparation is recorded as a failed run carrying the reason, then thrown again.</summary>
    Task<PipelineRunPreparation?> PrepareAutomatedRunAsync(
        int pipelineId,
        string triggerSource,
        Dictionary<string, string>? additionalVariables = null,
        CancellationToken ct = default);

    /// <summary>
    /// F-EXEC-1b (chaining): launch a child pipeline from a <c>trigger:</c> step or an <c>on_success:</c>
    /// hop. There is no caller principal at the chaining link, so the child run is authorized against the
    /// CHILD pipeline's OWNER - the upstream caller may administer one pipeline's servers but not the next
    /// hop's. Fail-closed: returns <c>null</c> without launching when the child is unowned or its owner
    /// lacks <see cref="Aetheus.Shared.Components.Auth.Permission.Admin"/> on a resolvable target (audited as
    /// <c>BlockedUnauthorizedChainedRun</c>).
    /// </summary>
    Task<PipelineRunDto?> TriggerChainedRunAsync(int childPipelineId, Dictionary<string, string> upstreamVariables, CancellationToken ct = default);

    /// <summary>
    /// Atomically mirrors a terminal child run onto its waiting trigger step and advances the parent
    /// run under the same per-run lock. This prevents parallel child completions from observing a
    /// half-updated dependency graph and falsely declaring the parent deadlocked.
    /// </summary>
    Task<bool> ResolveCompletedTriggerStepAsync(
        PipelineStepRun step,
        PipelineStatus childStatus,
        IReadOnlyDictionary<string, string> childOutputs,
        CancellationToken ct = default);

    /// <summary>Re-evaluates an active run from persisted step state without requiring a new task
    /// completion callback. Used by the scheduler self-heal after a lost advancement signal.</summary>
    Task ReconcileRunAsync(int pipelineRunId, CancellationToken ct = default);

    /// <summary>Cancellation records the actor and reason named by <paramref name="cancellation"/>
    /// on the run and its triggered children.</summary>
    Task<bool> CancelRunAsync(int runId, PipelineRunCancellation cancellation, CancellationToken ct = default);

    /// <summary>Force-completes a run wedged in Running as Failed (reconcile backstop). Used by the
    /// trigger-reconcile sweep for runs that can no longer make progress - e.g. a step failed but the run
    /// never finalized, or the run outlived every task without a completion event. Dispatches the normal
    /// completion event so waiting parent trigger steps unblock too.</summary>
    Task FailStuckRunAsync(int runId, CancellationToken ct = default);

    Task<bool> ResumeAfterApprovalAsync(int runId, CancellationToken ct = default);

    /// <summary>Applies a refused or expired approval; see <see cref="IPipelineRunControlService.ApplyRefusalAsync"/>.</summary>
    Task<PipelineStatus?> ApplyRefusalAsync(int runId, CancellationToken ct = default);

    /// <summary>Re-runs ONLY the failed steps of a previously-failed run (resets them to Pending and
    /// re-schedules), instead of starting a whole new run. Returns the updated run, or null if the
    /// run is not found, not in a Failed state, or has no failed steps to retry.</summary>
    Task<PipelineRunDto?> RetryFailedStepsAsync(int runId, CancellationToken ct = default);

    /// <summary>G: re-launches a run from an existing one. <see cref="RerunMode.Current"/> starts a
    /// fresh run from the live definition; the snapshot modes replay the source run's captured YAML,
    /// pinned to the same commit or floated to the branch head. Null when the source run is unknown.</summary>
    Task<PipelineRunDto?> RerunAsync(int sourceRunId, RerunMode mode, CancellationToken ct = default);

    /// <summary>BACK-020: resolves what a rerun in <paramref name="mode"/> will execute (snapshot, commit
    /// and replayed parameters) without launching it, so the caller authorizes its exact targets and then
    /// starts it with <see cref="TriggerPreparedRunAsync"/>. Null when the source run is unknown or the
    /// mode needs a snapshot it does not have.</summary>
    Task<PipelineRerunPlan?> PrepareRerunAsync(int sourceRunId, RerunMode mode, CancellationToken ct = default);

    /// <summary>BACK-020: the servers a retry of <paramref name="runId"/> can reach, read from the
    /// definition the run captured (the one a retry executes). Null when the run or its definition is
    /// unavailable.</summary>
    Task<IReadOnlyCollection<int>?> GetRetryTargetServerIdsAsync(int runId, CancellationToken ct = default);
    Task<PipelineCheckpointResumePreviewDto?> GetCheckpointResumePreviewAsync(int sourceRunId, CancellationToken ct = default);


    /// <summary>Returns the pipeline id and owning project id for a run.
    /// Null when the run does not exist.</summary>
    Task<(int PipelineId, int? ProjectId)?> GetRunPipelineContextAsync(int runId, CancellationToken ct = default);
    Task<int?> GetPipelineIdForRunAsync(int runId, CancellationToken ct = default);
    Task<int?> GetPipelineIdForApprovalAsync(int approvalId, CancellationToken ct = default);
    Task<bool> IsServerAssignedToRunAsync(int runId, int serverId, CancellationToken ct = default);

    /// <summary>True when a step of this "run to end" run failed and only went on because of that
    /// mode. Such a run may produce no release: it is past a failure a normal run would have stopped at.</summary>
    Task<bool> HasRunToEndFailureAsync(int runId, CancellationToken ct = default);
}
