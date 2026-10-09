// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// <see cref="IPipelineRepository"/> facade. Its surface is implemented by focused
/// collaborators for core data access, server resolution, coverage/quality, lifecycle, run lineage, and dependency graph queries,
/// composed here from the shared <see cref="AppDbContext"/> (replaces the former partial split).
/// Registered as the single <see cref="IPipelineRepository"/> in DI; callers are unaffected.
/// </summary>
public sealed class PipelineRepository(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<PipelineRepository> logger,
    IPipelineTaskLifecycle taskLifecycle,
    IPipelineRunLineageReader runLineage) : IPipelineRepository
{
    private readonly PipelineCoreRepository _core = new(
        db, timeProvider, logger, taskLifecycle);
    private readonly PipelineServerResolver _resolver = new(db);
    private readonly PipelineCoverageRepository _coverage = new(db);
    private readonly PipelineLifecycleRepository _lifecycle = new(db, timeProvider);
    private readonly IPipelineRunLineageReader _runLineage = runLineage;
    private readonly PipelineDependencyGraphRepository _dependencyGraph = new(db);
    private readonly PipelineTemplateRepository _templates = new(db);
    private readonly PipelineTaskQueueRepository _taskQueue = new(db);
    private readonly PipelineRunDetailRepository _runDetail = new(db);
    private readonly ProjectReleaseNumberRepository _releaseNumbers = new(db);

    // --- Core: pipelines, runs, steps, templates, artifacts, approvals ---
    public Task<(List<Pipeline> Items, int TotalCount)> GetPipelinesPagedAsync(string? search, PipelineTriggerType? triggerType, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default)
        => _core.GetPipelinesPagedAsync(search, triggerType, page, pageSize, accessibleIds, ct);

    public Task<(List<PipelineDto> Items, int TotalCount)> GetPipelinesPagedProjectedAsync(string? search, PipelineTriggerType? triggerType, int? environmentId, int? projectServerId, int? projectId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default)
        => _core.GetPipelinesPagedProjectedAsync(search, triggerType, environmentId, projectServerId, projectId, page, pageSize, accessibleIds, ct);

    public Task<List<PipelineDto>> GetPipelinesForDependencyGraphAsync(
        List<int>? accessibleIds = null, int? serverId = null, int? projectId = null,
        CancellationToken ct = default)
        => _dependencyGraph.GetAsync(accessibleIds, serverId, projectId, ct);

    public Task<(List<PipelineDto> Items, List<PipelineDto> Identities, int TotalCount)> GetPipelineDependencyPageAsync(
        PipelinePaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default)
        => _dependencyGraph.GetPageAsync(request, accessibleIds, ct);

    public Task<Pipeline?> GetPipelineWithRunsAsync(int id, CancellationToken ct = default)
        => _core.GetPipelineWithRunsAsync(id, ct);

    public Task<Pipeline?> FindPipelineAsync(int id, CancellationToken ct = default)
        => _core.FindPipelineAsync(id, ct);

    public Task<Pipeline?> FindPipelineByNameAndProjectAsync(string name, int projectId, CancellationToken ct = default)
        => _coverage.FindPipelineByNameAndProjectAsync(name, projectId, ct);

    public Task<List<(int Id, string Name, string Yaml)>> GetPipelineDefinitionsForProjectAsync(int projectId, CancellationToken ct = default)
        => _coverage.GetPipelineDefinitionsForProjectAsync(projectId, ct);

    public Task<List<StepTimingRow>> GetRecentSuccessfulStepTimingsAsync(int pipelineId, int runId, int take, CancellationToken ct = default)
        => _coverage.GetRecentSuccessfulStepTimingsAsync(pipelineId, runId, take, ct);

    public Task AddPipelineAsync(Pipeline pipeline, CancellationToken ct = default)
        => _core.AddPipelineAsync(pipeline, ct);

    public Task RemovePipelineAsync(Pipeline pipeline, CancellationToken ct = default)
        => _core.RemovePipelineAsync(pipeline, ct);

    public Task AddPipelineRunAsync(PipelineRun run, CancellationToken ct = default)
        => _core.AddPipelineRunAsync(run, ct);

    public Task<(PipelineRun Run, bool Created)> GetOrAddPipelineRunAsync(
        PipelineRun run, CancellationToken ct = default)
        => _core.GetOrAddPipelineRunAsync(run, ct);

    public Task<int> ReserveNextBuildNumberAsync(int pipelineId, CancellationToken ct = default)
        => _core.ReserveNextBuildNumberAsync(pipelineId, ct);

    public Task<int?> AllocateNextReleaseNumberAsync(int projectId, CancellationToken ct = default)
        => _releaseNumbers.AllocateNextAsync(projectId, ct);

    public Task<int?> GetLatestReleaseNumberAsync(int projectId, CancellationToken ct = default)
        => _releaseNumbers.GetLatestAsync(projectId, ct);

    public Task<int?> GetRunReleaseNumberAsync(int runId, CancellationToken ct = default)
        => _releaseNumbers.GetRunReleaseNumberAsync(runId, ct);

    public void TrackPipelineStepRun(PipelineStepRun stepRun)
        => _core.TrackPipelineStepRun(stepRun);

    public Task<List<PipelineStepRun>> GetPendingStepRunsAsync(int runId, CancellationToken ct = default)
        => _core.GetPendingStepRunsAsync(runId, ct);

    public void TrackTask(ServerTask task)
        => _core.TrackTask(task);

    public void TrackArtifactInput(PipelineRunArtifactInput input)
        => _core.TrackArtifactInput(input);

    public void TrackDastExecutionLease(DastExecutionLease lease)
        => _core.TrackDastExecutionLease(lease);

    public Task<List<PipelineRun>> GetRunsAsync(int pipelineId, int count, CancellationToken ct = default)
        => _core.GetRunsAsync(pipelineId, count, ct);

    public Task<(List<PipelineRunDto> Items, int TotalCount)> GetRunsPagedAsync(int pipelineId, int page, int pageSize, PipelineRunPaginationRequest? request = null, CancellationToken ct = default)
        => _core.GetRunsPagedAsync(pipelineId, page, pageSize, request, ct);

    public Task<List<PipelineRunDto>> GetActiveRunsAsync(List<int>? accessiblePipelineIds = null, int? projectId = null, CancellationToken ct = default)
        => _lifecycle.GetActiveRunsAsync(accessiblePipelineIds, projectId, ct);

    public Task<List<PipelineRunDto>> GetRecentRunsAsync(List<int>? accessiblePipelineIds = null, int? projectId = null, int? serverId = null, CancellationToken ct = default)
        => _lifecycle.GetRecentRunsAsync(accessiblePipelineIds, projectId, serverId, ct);

    public Task<PipelineRun?> GetRunDetailAsync(int runId, CancellationToken ct = default)
        => _runDetail.GetRunDetailAsync(runId, ct);

    public Task<PipelineRunResultSummaries> GetRunResultSummariesAsync(PipelineRun run, CancellationToken ct = default)
        => _runDetail.GetRunResultSummariesAsync(run, ct);

    public Task<PipelineSourceFields?> GetPipelineSourceFieldsAsync(int id, CancellationToken ct = default)
        => _core.GetPipelineSourceFieldsAsync(id, ct);

    public Task<PipelineTestResultSummaryDto?> GetTestResultSummaryAsync(int runId, CancellationToken ct = default)
        => _coverage.GetTestResultSummaryAsync(runId, ct);

    public async Task<PipelineRunDto> HydrateLinkedGradeAsync(PipelineRunDto run, CancellationToken ct = default)
        => (await PipelineRunGradeAggregation.ApplyAsync(db, [run], ct).ConfigureAwait(false))[0];

    public Task<Dictionary<int, TaskQueuePosition>> GetTaskQueuePositionsAsync(
        IReadOnlyCollection<int> taskIds, CancellationToken ct = default)
        => _taskQueue.GetPositionsAsync(taskIds, ct);

    public Task<List<PipelineRunQueueReference>> GetRunQueueReferencesAsync(
        int runId, CancellationToken ct = default)
        => _taskQueue.GetRunReferencesAsync(runId, ct);

    public Task<List<StepOutputProjection>> GetSuccessfulStepOutputsAsync(int runId, CancellationToken ct = default)
        => _core.GetSuccessfulStepOutputsAsync(runId, ct);

    public Task<PipelineRun?> GetPipelineRunWithPipelineAsync(int runId, CancellationToken ct = default)
        => _core.GetPipelineRunWithPipelineAsync(runId, ct);

    public Task<Dictionary<int, PipelineRunRootReference>> GetRootRunReferencesAsync(
        IReadOnlyCollection<int> runIds, CancellationToken ct = default)
        => _runLineage.GetRootRunReferencesAsync(runIds, ct);

    public Task<bool> AreAllStepsInStageCompletedAsync(int runId, string stageName, CancellationToken ct = default)
        => _core.AreAllStepsInStageCompletedAsync(runId, stageName, ct);

    public Task<List<PipelineStepRun>> FindStepRunsByTriggeredRunIdAsync(int triggeredRunId, CancellationToken ct = default)
        => _core.FindStepRunsByTriggeredRunIdAsync(triggeredRunId, ct);
    public Task<bool> TryResolveTriggeredStepAsync(
        int stepId,
        TaskExecutionStatus status,
        int exitCode,
        string? outputVariablesJson,
        string? failureCode,
        string? failureReason,
        DateTime completedAt,
        CancellationToken ct = default) =>
        _core.TryResolveTriggeredStepAsync(
            stepId, status, exitCode, outputVariablesJson, failureCode, failureReason, completedAt, ct);

    public Task<List<int>> GetTriggeredChildRunIdsAsync(int parentRunId, CancellationToken ct = default)
        => _core.GetTriggeredChildRunIdsAsync(parentRunId, ct);

    public Task<int?> FindTriggeredRunIdByPipelineNameAsync(int parentRunId, string pipelineName, CancellationToken ct = default)
        => _core.FindTriggeredRunIdByPipelineNameAsync(parentRunId, pipelineName, ct);

    public Task<List<PipelineStepRun>> GetStuckRunningTriggerStepsAsync(DateTime startedBefore, CancellationToken ct = default)
        => _lifecycle.GetStuckRunningTriggerStepsAsync(startedBefore, ct);

    public Task<Dictionary<int, PipelineStatus>> GetRunStatusesByIdsAsync(IReadOnlyCollection<int> runIds, CancellationToken ct = default)
        => _lifecycle.GetRunStatusesByIdsAsync(runIds, ct);

    public Task<bool> DidStagesAllSucceedAsync(int runId, IReadOnlyCollection<string> stageNames, CancellationToken ct = default)
        => _lifecycle.DidStagesAllSucceedAsync(runId, stageNames, ct);

    public Task<List<int>> GetStalledSchedulableRunIdsAsync(DateTime startedBefore, CancellationToken ct = default)
        => _lifecycle.GetStalledSchedulableRunIdsAsync(startedBefore, ct);

    public Task<List<int>> GetStalledCancellationRunIdsAsync(DateTime startedBefore, CancellationToken ct = default)
        => _lifecycle.GetStalledCancellationRunIdsAsync(startedBefore, ct);

    public Task<List<int>> GetStuckRunningRunIdsAsync(DateTime startedBefore, CancellationToken ct = default)
        => _lifecycle.GetStuckRunningRunIdsAsync(startedBefore, ct);

    public Task<bool> HasAnyStepFailedInStageAsync(int runId, string stageName, CancellationToken ct = default)
        => _core.HasAnyStepFailedInStageAsync(runId, stageName, ct);

    public Task<bool> HasAnyFailedStepInRunAsync(int runId, CancellationToken ct = default)
        => _core.HasAnyFailedStepInRunAsync(runId, ct);

    public Task<bool> HasFailedArtifactCollectionAsync(int runId, CancellationToken ct = default)
        => _core.HasFailedArtifactCollectionAsync(runId, ct);

    public Task<bool> HasAnyContinuableFailedStepInRunAsync(int runId, CancellationToken ct = default)
        => _core.HasAnyContinuableFailedStepInRunAsync(runId, ct);

    public Task<bool> HasAnySucceededStepInRunAsync(int runId, CancellationToken ct = default)
        => _core.HasAnySucceededStepInRunAsync(runId, ct);

    public Task<List<string>> GetCompletedStageNamesAsync(int runId, CancellationToken ct = default)
        => _core.GetCompletedStageNamesAsync(runId, ct);

    public Task<List<string>> GetTerminalStageNamesAsync(int runId, CancellationToken ct = default)
        => _core.GetTerminalStageNamesAsync(runId, ct);

    public Task<bool> HasActiveArtifactCollectionAsync(int runId, CancellationToken ct = default)
        => _core.HasActiveArtifactCollectionAsync(runId, ct);

    public Task<bool> IsRunStillRunningAsync(int runId, CancellationToken ct = default)
        => _core.IsRunStillRunningAsync(runId, ct);

    public Task UpdatePipelineRunStatusAsync(int runId, PipelineStatus status, CancellationToken ct = default)
        => _core.UpdatePipelineRunStatusAsync(runId, status, ct);

    public Task<bool> TryTransitionPipelineRunStatusAsync(int runId, PipelineStatus expectedStatus, PipelineStatus newStatus, CancellationToken ct = default)
        => _lifecycle.TryTransitionPipelineRunStatusAsync(runId, expectedStatus, newStatus, ct);

    public Task AppendRunWarningsAsync(int runId, IReadOnlyCollection<string> warnings, CancellationToken ct = default)
        => _core.AppendRunWarningsAsync(runId, warnings, ct);

    public Task<bool> SetRunWaitingReasonAsync(int runId, string? reason, CancellationToken ct = default)
        => _core.SetRunWaitingReasonAsync(runId, reason, ct);

    public Task<List<PipelineTemplate>> GetTemplatesAsync(CancellationToken ct = default)
        => _templates.GetTemplatesAsync(ct);

    public Task<List<PipelineTemplateSummaryDto>> GetTemplateSummariesAsync(CancellationToken ct = default)
        => _templates.GetTemplateSummariesAsync(ct);

    public Task<List<string>> GetTemplateVersionYamlDefinitionsByOrganizationAsync(
        int organizationId, int excludedTemplateId, CancellationToken ct = default)
        => _templates.GetTemplateVersionYamlDefinitionsByOrganizationAsync(
            organizationId, excludedTemplateId, ct);

    public Task<PipelineTemplate?> GetTemplateAsync(int id, CancellationToken ct = default)
        => _templates.GetTemplateAsync(id, ct);

    public Task<PipelineTemplate?> FindTemplateAsync(int id, CancellationToken ct = default)
        => _templates.FindTemplateAsync(id, ct);

    public Task AddTemplateAsync(PipelineTemplate template, CancellationToken ct = default)
        => _templates.AddTemplateAsync(template, ct);

    public Task RemoveTemplateAsync(PipelineTemplate template, CancellationToken ct = default)
        => _templates.RemoveTemplateAsync(template, ct);

    public Task CancelActiveStepRunsAndTasksAsync(int runId, CancellationToken ct = default)
        => _core.CancelActiveStepRunsAndTasksAsync(runId, ct);

    public Task CancelPendingStepRunsAsync(int runId, CancellationToken ct = default)
        => _core.CancelPendingStepRunsAsync(runId, ct);

    public Task RequestPipelineRunCancellationAsync(int runId, PipelineRunCancellation cancellation, CancellationToken ct = default)
        => _core.RequestPipelineRunCancellationAsync(runId, cancellation, ct);

    public Task CancelPendingStepRunsExceptStagesAsync(
        int runId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct = default)
        => _core.CancelPendingStepRunsExceptStagesAsync(runId, preservedStages, ct);

    public Task CancelOrphanedRunningStepRunsExceptStagesAsync(
        int runId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct = default)
        => _core.CancelOrphanedRunningStepRunsExceptStagesAsync(runId, preservedStages, ct);

    public Task<int> ResetFailedStepRunsAsync(int runId, CancellationToken ct = default)
        => _lifecycle.ResetFailedStepRunsAsync(runId, ct);

    public Task<List<PipelineArtifact>> GetArtifactsAsync(int runId, CancellationToken ct = default)
        => _core.GetArtifactsAsync(runId, ct);

    public Task AddArtifactAsync(PipelineArtifact artifact, CancellationToken ct = default)
        => _core.AddArtifactAsync(artifact, ct);

    public Task<List<PipelineApproval>> GetApprovalsAsync(int runId, CancellationToken ct = default)
        => _core.GetApprovalsAsync(runId, ct);

    public Task<PipelineApproval?> FindApprovalAsync(int approvalId, CancellationToken ct = default)
        => _core.FindApprovalAsync(approvalId, ct);

    public Task<List<PendingApprovalDto>> GetPendingApprovalsAsync(
        List<int>? accessiblePipelineIds, CancellationToken ct = default)
        => _lifecycle.GetPendingApprovalsAsync(accessiblePipelineIds, ct);

    public Task<List<int>> GetExpiredPendingApprovalIdsAsync(DateTime now, CancellationToken ct = default)
        => _lifecycle.GetExpiredPendingApprovalIdsAsync(now, ct);

    public Task<List<StrandedApprovalRun>> GetStrandedApprovalRunsAsync(CancellationToken ct = default)
        => _lifecycle.GetStrandedApprovalRunsAsync(ct);

    public Task<int> CloseApprovalsOfEndedRunsAsync(int? runId, DateTime now, string reason, CancellationToken ct = default)
        => _lifecycle.CloseApprovalsOfEndedRunsAsync(runId, now, reason, ct);

    public Task<string?> FindPublishedReleaseVersionByRunIdAsync(int pipelineRunId, CancellationToken ct = default)
        => _lifecycle.FindPublishedReleaseVersionByRunIdAsync(pipelineRunId, ct);

    public Task<PipelineApproval?> TryResolveApprovalAsync(int approvalId, ApprovalStatus decision, DateTime resolvedAt,
        int? resolvedByUserId, string? comments, CancellationToken ct = default)
        => _lifecycle.TryResolveApprovalAsync(approvalId, decision, resolvedAt, resolvedByUserId, comments, ct);

    public Task AddApprovalAsync(PipelineApproval approval, CancellationToken ct = default)
        => _core.AddApprovalAsync(approval, ct);

    public Task<Data.Entities.Environment?> FindEnvironmentByNameAsync(string name, int? projectId, CancellationToken ct = default)
        => _core.FindEnvironmentByNameAsync(name, projectId, ct);

    public Task<int?> GetRunProjectIdAsync(int runId, CancellationToken ct = default)
        => _core.GetRunProjectIdAsync(runId, ct);

    public Task<Data.Entities.Environment?> FindEnvironmentByNameForProjectAsync(string name, int projectId, CancellationToken ct = default)
        => _core.FindEnvironmentByNameForProjectAsync(name, projectId, ct);

    public Task<PipelineTemplate?> FindTemplateByNameAsync(string name, CancellationToken ct = default)
        => _templates.FindTemplateByNameAsync(name, ct);

    public Task<PipelineTemplate?> FindTemplateByNameAsync(string name, int organizationId, CancellationToken ct = default)
        => _templates.FindTemplateByNameAsync(name, organizationId, ct);

    public Task<PipelineTemplate?> FindSharedTemplateByNameAsync(string name, CancellationToken ct = default)
        => _templates.FindSharedTemplateByNameAsync(name, ct);

    public Task<PipelineTemplateVersion?> GetTemplateVersionAsync(int templateId, int version, CancellationToken ct = default)
        => _templates.GetTemplateVersionAsync(templateId, version, ct);

    public Task<(List<PipelineTemplateVersionSummaryDto> Items, int TotalCount)> GetTemplateVersionsPagedAsync(
        int templateId, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
        => _templates.GetTemplateVersionsPagedAsync(
            templateId, page, pageSize, sortBy, sortDescending, ct, columnFilters);

    public Task<List<string>> GetPipelineYamlDefinitionsByOrganizationAsync(
        int organizationId, CancellationToken ct = default)
        => _templates.GetPipelineYamlDefinitionsByOrganizationAsync(organizationId, ct);

    public Task<List<TestResult>> GetTestResultsAsync(int runId, CancellationToken ct = default)
        => _core.GetTestResultsAsync(runId, ct);

    public Task AddTestResultsAsync(IEnumerable<TestResult> results, CancellationToken ct = default)
        => _core.AddTestResultsAsync(results, ct);

    public Task<List<PipelineStepRun>> GetFailedStepRunsInStageAsync(int runId, string stageName, CancellationToken ct = default)
        => _core.GetFailedStepRunsInStageAsync(runId, stageName, ct);

    public Task<List<EnvironmentCheck>> GetEnvironmentChecksAsync(int environmentId, CancellationToken ct = default)
        => _core.GetEnvironmentChecksAsync(environmentId, ct);

    public Task<bool> IsStepRetryEligibleAsync(int pipelineRunId, int stepRunId, CancellationToken ct = default)
        => _lifecycle.IsStepRetryEligibleAsync(pipelineRunId, stepRunId, ct);

    public Task<int?> GetPipelineIdForRunAsync(int runId, CancellationToken ct = default)
        => _core.GetPipelineIdForRunAsync(runId, ct);

    public Task<bool> IsServerAssignedToRunAsync(int runId, int serverId, CancellationToken ct = default)
        => _core.IsServerAssignedToRunAsync(runId, serverId, ct);

    public Task<string?> FindRunningStageNameAsync(int runId, int serverId, CancellationToken ct = default)
        => _core.FindRunningStageNameAsync(runId, serverId, ct);

    public Task<int?> GetPipelineIdForApprovalAsync(int approvalId, CancellationToken ct = default)
        => _core.GetPipelineIdForApprovalAsync(approvalId, ct);

    public Task<int?> GetPipelineIdForArtifactAsync(int artifactId, CancellationToken ct = default)
        => _core.GetPipelineIdForArtifactAsync(artifactId, ct);

    public Task SaveChangesAsync(CancellationToken ct = default)
        => _core.SaveChangesAsync(ct);

    // --- Server resolution ---
    public Task<List<Pipeline>> GetPipelinesByEnvironmentAsync(int environmentId, CancellationToken ct = default)
        => _resolver.GetPipelinesByEnvironmentAsync(environmentId, ct);

    public Task<(string Name, int ProjectId)?> GetEnvironmentCopyTargetAsync(int environmentId, CancellationToken ct = default)
        => _resolver.GetEnvironmentCopyTargetAsync(environmentId, ct);

    public Task<Server?> FindOnlineServerByAgentAsync(string agent, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
        => _resolver.FindOnlineServerByAgentAsync(agent, requiredOs, ct);

    public Task<Server?> FindOnlineServerByAgentInOrganizationAsync(string agent, OsType requiredOs, int organizationId, CancellationToken ct = default)
        => _resolver.FindOnlineServerByAgentInOrganizationAsync(agent, requiredOs, organizationId, ct);

    public Task<Server?> FindOnlineServerInPoolAsync(string poolName, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
        => _resolver.FindOnlineServerInPoolAsync(poolName, requiredOs, ct);

    public Task<Server?> FindOnlineServerInPoolInOrganizationAsync(string poolName, OsType requiredOs, int organizationId, CancellationToken ct = default)
        => _resolver.FindOnlineServerInPoolInOrganizationAsync(poolName, requiredOs, organizationId, ct);

    public Task<Server?> FindOnlineServerInEnvironmentAsync(string environmentName, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
        => _resolver.FindOnlineServerInEnvironmentAsync(environmentName, requiredOs, ct);

    public Task<Server?> FindOnlineServerInEnvironmentInOrganizationAsync(string environmentName, OsType requiredOs, int organizationId, CancellationToken ct = default)
        => _resolver.FindOnlineServerInEnvironmentInOrganizationAsync(environmentName, requiredOs, organizationId, ct);

    public Task<Server?> FindAnyOnlineRunnerAsync(int? organizationId, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
        => _resolver.FindAnyOnlineRunnerAsync(organizationId, requiredOs, ct);

    public Task<bool> HasRunnerWithDockerAsync(int? organizationId, CancellationToken ct = default)
        => _resolver.HasRunnerWithDockerAsync(organizationId, ct);

    public Task<Server?> FindOnlineDeployTargetAsync(string? pool, string? environment, string? agent, OsType requiredOs, int? organizationId, CancellationToken ct = default)
        => _resolver.FindOnlineDeployTargetAsync(pool, environment, agent, requiredOs, organizationId, ct);

    public Task<List<int>> FindCandidateTargetServerIdsAsync(string? pool, string? environment, string? agent, OsType requiredOs, int? organizationId, bool deploymentStage, CancellationToken ct = default)
        => _resolver.FindCandidateTargetServerIdsAsync(pool, environment, agent, requiredOs, organizationId, deploymentStage, ct);

    public Task<Server?> FindOnlineServerByIdAsync(int serverId, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
        => _resolver.FindOnlineServerByIdAsync(serverId, requiredOs, ct);

    public Task<Server?> FindServerByIdAsync(int serverId, CancellationToken ct = default)
        => _resolver.FindServerByIdAsync(serverId, ct);

    public Task<int?> GetRunAffinityServerIdAsync(int runId, CancellationToken ct = default)
        => _resolver.GetRunAffinityServerIdAsync(runId, ct);

    public Task<List<PipelineRunnerFacts>> GetRunnerFactsAsync(IReadOnlyCollection<int> serverIds, CancellationToken ct = default)
        => _resolver.GetRunnerFactsAsync(serverIds, ct);

    public Task<int?> GetStageProducerServerIdAsync(int runId, string stageName, CancellationToken ct = default)
        => _resolver.GetStageProducerServerIdAsync(runId, stageName, ct);

    public Task<int?> GetPipelineOrganizationIdAsync(int pipelineId, CancellationToken ct = default)
        => _resolver.GetPipelineOrganizationIdAsync(pipelineId, ct);

    public Task<int?> GetPipelineOwnerOrganizationIdAsync(
        int? projectId, int? environmentId, int? projectServerId, CancellationToken ct = default)
        => _resolver.GetPipelineOwnerOrganizationIdAsync(projectId, environmentId, projectServerId, ct);

    public Task<int?> GetPipelineProjectIdAsync(Pipeline pipeline, CancellationToken ct = default)
        => _resolver.GetPipelineProjectIdAsync(pipeline, ct);

    public Task<DeploymentGateRelease?> FindDeploymentGateReleaseAsync(
        int projectId, string releaseSelector, CancellationToken ct = default)
        => _resolver.FindDeploymentGateReleaseAsync(projectId, releaseSelector, ct);

    public Task<BranchAdvanceRelease?> FindBranchAdvanceReleaseAsync(
        int projectId, string version, CancellationToken ct = default)
        => _resolver.FindBranchAdvanceReleaseAsync(projectId, version, ct);

    public Task<int?> GetProjectServerProjectIdAsync(int projectServerId, CancellationToken ct = default)
        => _resolver.GetProjectServerProjectIdAsync(projectServerId, ct);

    public Task<string?> GetProjectReleasePatternAsync(int projectId, CancellationToken ct = default)
        => _resolver.GetProjectReleasePatternAsync(projectId, ct);

    public Task<string?> GetProjectOwnerUsernameAsync(int projectId, CancellationToken ct = default)
        => _resolver.GetProjectOwnerUsernameAsync(projectId, ct);

    public Task<bool> IsActiveUsernameAsync(string username, CancellationToken ct = default)
        => _resolver.IsActiveUsernameAsync(username, ct);

    public Task<List<int>> FindServerIdsByAgentAsync(string? agent, CancellationToken ct = default)
        => _resolver.FindServerIdsByAgentAsync(agent, ct);

    public Task<List<int>> FindServerIdsInPoolAsync(string poolName, CancellationToken ct = default)
        => _resolver.FindServerIdsInPoolAsync(poolName, ct);

    public Task<List<int>> FindServerIdsInEnvironmentAsync(string environmentName, CancellationToken ct = default)
        => _resolver.FindServerIdsInEnvironmentAsync(environmentName, ct);

    // --- Coverage / code-quality ---
    public Task<bool> HasAnyRunningStepInRunAsync(int runId, CancellationToken ct = default)
        => _coverage.HasAnyRunningStepInRunAsync(runId, ct);

    public Task<List<string>> GetActiveStageNamesAsync(int runId, CancellationToken ct = default)
        => _coverage.GetActiveStageNamesAsync(runId, ct);

    public Task<List<CoverageResult>> GetCoverageResultsAsync(int runId, CancellationToken ct = default)
        => _coverage.GetCoverageResultsAsync(runId, ct);

    public Task AddCoverageResultAsync(CoverageResult result, CancellationToken ct = default)
        => _coverage.AddCoverageResultAsync(result, ct);

    public Task<List<LintResult>> GetLintResultsAsync(int runId, CancellationToken ct = default)
        => _coverage.GetLintResultsAsync(runId, ct);

    public Task AddLintResultAsync(LintResult result, CancellationToken ct = default)
        => _coverage.AddLintResultAsync(result, ct);

    public Task<List<CoverageTrendRow>> GetCoverageTrendAsync(int runId, int take, CancellationToken ct = default)
        => _coverage.GetCoverageTrendAsync(runId, take, ct);

    public Task<List<ComplexityTrendRow>> GetComplexityTrendAsync(int runId, int take, CancellationToken ct = default)
        => _coverage.GetComplexityTrendAsync(runId, take, ct);

    public Task<List<CoverageTrendRow>> GetProjectCoverageTrendAsync(int projectId, int take, CancellationToken ct = default)
        => _coverage.GetProjectCoverageTrendAsync(projectId, take, ct);

    public Task<List<ComplexityTrendRow>> GetProjectComplexityTrendAsync(int projectId, int take, CancellationToken ct = default)
        => _coverage.GetProjectComplexityTrendAsync(projectId, take, ct);

    public Task<List<TestTrendRow>> GetProjectTestTrendAsync(int projectId, int take, CancellationToken ct = default)
        => _coverage.GetProjectTestTrendAsync(projectId, take, ct);

    public Task<List<RunMetric>> GetRunMetricsAsync(int runId, CancellationToken ct = default)
        => _coverage.GetRunMetricsAsync(runId, ct);

    public Task AddRunMetricsAsync(IEnumerable<RunMetric> metrics, CancellationToken ct = default)
        => _coverage.AddRunMetricsAsync(metrics, ct);

    // --- Lifecycle (retention, triggers, active-run checks) ---
    public Task<int> DeleteRunsOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
        => _lifecycle.DeleteRunsOlderThanAsync(cutoff, ct);

    public Task<List<Pipeline>> GetWebhookTriggeredPipelinesWithProjectAsync(CancellationToken ct = default)
        => _lifecycle.GetWebhookTriggeredPipelinesWithProjectAsync(ct);

    public Task<List<Pipeline>> GetWebhookTriggeredPipelinesForProjectAsync(int projectId, CancellationToken ct = default)
        => _lifecycle.GetWebhookTriggeredPipelinesForProjectAsync(projectId, ct);

    public Task<List<Pipeline>> GetScheduledPipelinesAsync(CancellationToken ct = default)
        => _lifecycle.GetScheduledPipelinesAsync(ct);

    public Task<bool> HasActiveRunAsync(int pipelineId, CancellationToken ct = default)
        => _lifecycle.HasActiveRunAsync(pipelineId, ct);

    public Task<bool> LockPipelineForWebhookAsync(int pipelineId, CancellationToken ct = default)
        => _lifecycle.LockPipelineForWebhookAsync(pipelineId, ct);

    public Task<List<int>> GetActiveRunIdsAsync(int pipelineId, CancellationToken ct = default)
        => _lifecycle.GetActiveRunIdsAsync(pipelineId, ct);

    public Task<List<int>> GetAllActiveRunIdsAsync(CancellationToken ct = default)
        => _lifecycle.GetAllActiveRunIdsAsync(ct);

    public Task<HashSet<int>> GetPipelineIdsWithActiveRunsAsync(CancellationToken ct = default)
        => _lifecycle.GetPipelineIdsWithActiveRunsAsync(ct);
}
