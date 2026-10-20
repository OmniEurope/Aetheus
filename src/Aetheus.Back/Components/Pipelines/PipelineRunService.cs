// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.GitGraph;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

public class PipelineRunService(
    IPipelineRepository repo,
    IPipelineVariableResolver variableResolver,
    ILogger<PipelineRunService> logger,
    IPipelineRunFinalizer finalizer,
    IPipelineRunParameterResolver parameterResolver,
    IPipelineCheckpointReuseService checkpoints,
    IPipelineRunPreparationService preparations,
    IPipelineRunControlService control,
    IPipelineTriggerStepCoordinator triggerSteps,
    IPipelineRunLauncher launcher,
    IPipelineRunScheduler scheduler,
    IPipelineRunPreflightService preflight,
    IPipelineAdvisoryPreflightBuilder advisoryPreflight,
    IPipelineRunReader runReader,
    IPipelineRunDefinitionParser definitions) : IPipelineRunService, IPipelineLauncher
{
    internal const string SystemPrepareStage = "System:Prepare";
    internal const string SystemCleanupStage = PipelineSystemStages.Cleanup;
    internal const string CancellationRequestedVariable = "__AETHEUS_CANCEL_REQUESTED";
    // Who cancelled the run and why, kept beside the request marker for the run page.
    internal const string CancelledByVariable = "__AETHEUS_CANCELLED_BY";
    internal const string CancellationReasonVariable = "__AETHEUS_CANCEL_REASON";
    internal const string SourceCommitVariable = "AETHEUS_SOURCE_COMMIT";
    internal const string SourceBranchVariable = "AETHEUS_RUN_BRANCH";
    // Set only on a run whose workspace comes from another repository (source: block).
    internal const string DefinitionCommitVariable = "AETHEUS_DEFINITION_COMMIT";
    internal const string DefinitionBranchVariable = "AETHEUS_DEFINITION_BRANCH";
    internal const string ResumeSourceRunVariable = "AETHEUS_RESUME_SOURCE_RUN_ID";
    internal const string HostWorkspaceVariable = "AETHEUS_HOST_WORKSPACE";

    public async Task<PipelineRunDto?> TriggerRunAsync(int id, Dictionary<string, string>? additionalVariables = null,
        Dictionary<string, string>? parameters = null, CancellationToken ct = default)
        => await launcher.TriggerResolvedRunAsync(id, additionalVariables, parameters,
            yamlOverride: null, ResolveSourceCommit(additionalVariables), ct).ConfigureAwait(false);

    public Task<PipelineRunPreparation?> PrepareRunAsync(int pipelineId, string? sourceBranch = null,
        CancellationToken ct = default)
        => preparations.PrepareRunAsync(pipelineId, sourceBranch, yamlOverride: null, commitOverride: null, ct);

    public Task<PipelineRunPreparation?> PrepareRunAtCommitAsync(int pipelineId, string? sourceBranch, string commitHash,
        CancellationToken ct = default)
        => IsGitCommitHash(commitHash)
            ? preparations.PrepareRunAsync(pipelineId, sourceBranch, yamlOverride: null, commitOverride: commitHash, ct)
            : throw new BadRequestException("A run can only be pinned to a full commit hash.");


    public Task<List<PipelineRunParameterDto>> GetRunParametersAsync(
        int pipelineId, string? sourceBranch = null, CancellationToken ct = default)
        => preparations.GetRunParametersAsync(pipelineId, sourceBranch, ct);


    // --- Launching now lives in PipelineRunLauncher; the facade keeps the IPipelineRunService surface ---

    public Task<PipelineRunDto?> TriggerPreparedRunAsync(PipelineRunPreparation preparation,
        Dictionary<string, string>? additionalVariables = null,
        Dictionary<string, string>? parameters = null, CancellationToken ct = default,
        string? idempotencyKey = null)
        => launcher.TriggerPreparedRunAsync(preparation, additionalVariables, parameters, ct, idempotencyKey);

    public Task<PipelineRunPreparation> ResolveRunParametersAsync(
        PipelineRunPreparation preparation,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct = default)
        => parameterResolver.ResolveRunParametersAsync(preparation, parameters, ct);

    public Task<PipelineRunDto?> TriggerAutomatedRunAsync(
        int pipelineId, string triggerSource, Dictionary<string, string>? additionalVariables = null,
        CancellationToken ct = default)
        => launcher.TriggerAutomatedRunAsync(pipelineId, triggerSource, additionalVariables, ct);

    public Task<PipelineRunPreparation?> PrepareAutomatedRunAsync(
        int pipelineId, string triggerSource, Dictionary<string, string>? additionalVariables = null,
        CancellationToken ct = default)
        => launcher.PrepareAutomatedRunAsync(pipelineId, triggerSource, additionalVariables, ct);

    public Task<PipelineRunDto?> TriggerPreparedAutomatedRunAsync(
        PipelineRunPreparation preparation, string triggerSource,
        Dictionary<string, string>? additionalVariables = null, CancellationToken ct = default)
        => launcher.TriggerPreparedAutomatedRunAsync(preparation, triggerSource, additionalVariables, ct);

    public Task RecordRefusedAutomatedLaunchAsync(
        int pipelineId, string triggerSource, string reason,
        IReadOnlyDictionary<string, string>? additionalVariables = null, CancellationToken ct = default)
        => launcher.RecordRefusedAutomatedLaunchAsync(pipelineId, triggerSource, reason, additionalVariables, ct);

    public Task<PipelineRunDto?> RerunAsync(int sourceRunId, RerunMode mode, CancellationToken ct = default)
        => launcher.RerunAsync(sourceRunId, mode, ct);

    public Task<PipelineRerunPlan?> PrepareRerunAsync(int sourceRunId, RerunMode mode, CancellationToken ct = default)
        => launcher.PrepareRerunAsync(sourceRunId, mode, ct);

    public async Task<IReadOnlyCollection<int>?> GetRetryTargetServerIdsAsync(int runId, CancellationToken ct = default)
    {
        // BACK-020: a retry replays the run's captured definition, so that definition is what is authorized.
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (run?.Pipeline is null) return null;
        var definition = definitions.Parse(run);
        if (definition is null) return null;
        var organizationId = await repo.GetPipelineOrganizationIdAsync(run.Pipeline.Id, ct).ConfigureAwait(false);
        var projectId = await repo.GetRunProjectIdAsync(runId, ct).ConfigureAwait(false);
        return await parameterResolver.ResolveCandidateTargetServerIdsAsync(definition, organizationId, projectId, ct)
            .ConfigureAwait(false);
    }

    public Task<PipelineCheckpointResumePreviewDto?> GetCheckpointResumePreviewAsync(
        int sourceRunId,
        CancellationToken ct = default)
        => checkpoints.GetCheckpointResumePreviewAsync(sourceRunId, ct);

    public async Task<PaginatedResult<PipelineRunDto>> GetRunsAsync(int pipelineId, PipelineRunPaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        // Already projected server-side (slim list DTOs - no commands/output vars/yaml).
        var (runs, total) = await repo.GetRunsPagedAsync(pipelineId, page, pageSize, request, ct).ConfigureAwait(false);
        return new PaginatedResult<PipelineRunDto>
        {
            Items = runs,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<List<PipelineRunDto>> GetActiveRunsAsync(
        List<int>? accessiblePipelineIds = null, int? projectId = null, CancellationToken ct = default) =>
        repo.GetActiveRunsAsync(accessiblePipelineIds, projectId, ct);

    public Task<List<PipelineRunDto>> GetRecentRunsAsync(
        List<int>? accessiblePipelineIds = null, int? projectId = null, int? serverId = null,
        CancellationToken ct = default) =>
        repo.GetRecentRunsAsync(accessiblePipelineIds, projectId, serverId, ct);

    public Task<PipelineRunDto?> GetRunAsync(int runId, CancellationToken ct = default)
        => runReader.GetRunAsync(runId, ct);

    public async Task<PipelineRunQueueStateDto> GetRunQueueStateAsync(
        int runId,
        CancellationToken ct = default)
    {
        var references = await repo.GetRunQueueReferencesAsync(runId, ct).ConfigureAwait(false);
        var positions = await repo.GetTaskQueuePositionsAsync(
            references.Select(reference => reference.TaskId).ToArray(), ct).ConfigureAwait(false);
        return new PipelineRunQueueStateDto
        {
            RunId = runId,
            Steps = references
                .Where(reference => positions.ContainsKey(reference.TaskId))
                .Select(reference => new PipelineStepQueueStateDto
                {
                    StepId = reference.StepId,
                    TaskId = reference.TaskId,
                    Position = positions[reference.TaskId].Position,
                    Depth = positions[reference.TaskId].Depth
                })
                .ToList()
        };
    }

    public async Task<DryRunResultDto?> DryRunAsync(int pipelineId, Dictionary<string, string>? additionalVars = null, CancellationToken ct = default)
    {
        var pipeline = await repo.FindPipelineAsync(pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null) return null;

        var definition = YamlParsingHelper.ParseAndValidate(pipeline.YamlDefinition, logger);
        if (definition is null) return null;

        // S-TECH-D4XR: validate + preview any declared run parameters supplied among the additional
        // vars (same resolver the real trigger uses) - surfaced as warnings so the preview never throws.
        var paramWarnings = ValidateAndApplyParameters(definition, ref additionalVars);

        var dryRunProjectId = pipeline.ProjectId
            ?? await repo.GetPipelineProjectIdAsync(pipeline, ct).ConfigureAwait(false);
        var (resolvedVars, warnings, secretKeys) = await variableResolver.ResolveVariablesWithWarningsAsync(definition, dryRunProjectId, additionalVars, ct).ConfigureAwait(false);
        warnings.InsertRange(0, paramWarnings);

        var stages = new List<DryRunStageDto>();
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
        {
            // Merge stage-level variables on top
            var stageVars = new Dictionary<string, string>(resolvedVars, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in stage.Variables)
                stageVars[key] = value;

            // Mask secret values in resolved commands so dry-run output is safe to display.
            var maskedVars = MaskSecretValues(stageVars, secretKeys);

            var steps = stage.Steps.Select(step => new DryRunStepDto
            {
                StepName = step.Name,
                OriginalCommand = step.Shell,
                ResolvedCommand = SubstituteVariables(step.Shell, maskedVars)
            }).ToList();

            stages.Add(new DryRunStageDto
            {
                StageName = stage.Name,
                Agent = stage.Agent,
                Os = stage.Os,
                Steps = steps
            });
        }

        return new DryRunResultDto
        {
            Stages = stages,
            ResolvedVariables = FilterSecretKeys(resolvedVars, secretKeys),
            Warnings = warnings
        };
    }

    public async Task<PipelinePreflightDto?> PreflightAsync(
        int pipelineId, Dictionary<string, string>? additionalVars = null, CancellationToken ct = default)
    {
        var preparation = await preparations.PrepareRunAsync(pipelineId, ResolveRunBranch(additionalVars),
            yamlOverride: null, ResolveSourceCommit(additionalVars), ct).ConfigureAwait(false);
        return preparation is null
            ? null
            : await PreflightAsync(preparation, additionalVars, ct).ConfigureAwait(false);
    }

    public async Task<PipelinePreflightDto?> PreflightAsync(
        PipelineRunPreparation preparation, Dictionary<string, string>? additionalVars = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var pipeline = preparation.Pipeline;
        var definition = preparation.Definition;

        // S-TECH-D4XR: validate the supplied run parameters during preflight too, so a bad value is
        // reported alongside the unresolved-target warnings before the run is launched.
        var paramWarnings = ValidateAndApplyParameters(definition, ref additionalVars);

        var (resolvedVars, warnings, _) = await variableResolver.ResolveVariablesWithWarningsAsync(
            definition, preparation.EffectiveProjectId, additionalVars, ct).ConfigureAwait(false);
        warnings.InsertRange(0, paramWarnings);
        preflight.ValidateUniqueDeploymentTargets(definition, resolvedVars);

        var organizationId = await repo.GetPipelineOrganizationIdAsync(pipeline.Id, ct).ConfigureAwait(false);
        return await advisoryPreflight
            .BuildAsync(definition, resolvedVars, warnings, organizationId, preparation.EffectiveProjectId, ct)
            .ConfigureAwait(false);
    }

    // S-TECH-D4XR: pull the declared-parameter values out of the supplied additional vars, validate
    // them with the same resolver the real trigger uses, and merge the effective (supplied ?? default)
    // values back at highest precedence so the preview reflects them. Returns human-readable warnings
    // for any validation failure (preview is non-destructive, so we warn instead of throwing).
    internal static List<string> ValidateAndApplyParameters(PipelineYamlDefinition definition, ref Dictionary<string, string>? additionalVars)
    {
        if (definition.Parameters.Count == 0) return [];

        var supplied = additionalVars?
            .Where(kv => definition.Parameters.Any(p => p.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        PipelineParameterResolver.TryResolve(definition.Parameters, supplied, out var effective, out var errors);
        if (effective.Count > 0)
        {
            additionalVars ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in effective)
            {
                additionalVars[k] = v;
                // S-TECH-V6QN: also preview the ${{ parameters.X }} namespace.
                additionalVars[PipelineCommandBuilder.ParameterKeyPrefix + k] = v;
            }
        }
        return errors.Select(e => $"Parameter: {e}").ToList();
    }

    public async Task<IReadOnlyCollection<int>> ResolveCandidateTargetServerIdsAsync(
        int pipelineId, CancellationToken ct = default)
    {
        var preparation = await PrepareRunAsync(pipelineId, ct: ct).ConfigureAwait(false);
        return preparation?.TargetServerIds ?? [];
    }

    // --- Stage execution, task creation, server resolution, command building, cancel/retry/approval ---
    // --- Stage advancement now lives in PipelineRunScheduler; the launcher travels as a parameter ---

    public Task AdvanceStageAsync(int pipelineRunId, string completedStageName, CancellationToken ct = default)
        => scheduler.AdvanceStageAsync(pipelineRunId, completedStageName, launcher, ct);

    public Task ReconcileRunAsync(int pipelineRunId, CancellationToken ct = default)
        => scheduler.ReconcileRunAsync(pipelineRunId, launcher, ct);

    public Task ContinueAfterArtifactCollectionAsync(
        int pipelineRunId, TaskExecutionStatus collectionStatus, CancellationToken ct = default)
        => scheduler.ContinueAfterArtifactCollectionAsync(pipelineRunId, collectionStatus, launcher, ct);

    public Task<bool> ResolveCompletedTriggerStepAsync(
        PipelineStepRun step, PipelineStatus childStatus,
        IReadOnlyDictionary<string, string> childOutputs, CancellationToken ct = default)
        => scheduler.ResolveCompletedTriggerStepAsync(step, childStatus, childOutputs, launcher, ct);

    public Task<PipelineRunDto?> TriggerChainedRunAsync(
        int childPipelineId, Dictionary<string, string>? upstreamVariables, CancellationToken ct = default) =>
        triggerSteps.TriggerChainedRunAsync(childPipelineId, upstreamVariables, launcher, ct);

    public Task<bool> CancelRunAsync(int runId, PipelineRunCancellation cancellation, CancellationToken ct = default) =>
        control.CancelRunAsync(runId, cancellation, scheduler, launcher, ct);

    public Task FailStuckRunAsync(int runId, CancellationToken ct = default) =>
        finalizer.FailStuckRunAsync(runId, ct);

    public Task<bool> ResumeAfterApprovalAsync(int runId, CancellationToken ct = default) =>
        control.ResumeAfterApprovalAsync(runId, scheduler, launcher, ct);

    public Task<PipelineStatus?> ApplyRefusalAsync(int runId, CancellationToken ct = default) =>
        control.ApplyRefusalAsync(runId, scheduler, launcher, ct);

    public async Task<PipelineRunDto?> RetryFailedStepsAsync(int runId, CancellationToken ct = default)
        => await control.RetryFailedStepsAsync(runId, scheduler, launcher, ct).ConfigureAwait(false)
            ? await GetRunAsync(runId, ct).ConfigureAwait(false)
            : null;

    internal static Dictionary<string, string> DeserializeResolvedVariablesStatic(string? json)
        => DeserializeResolvedVariables(json);

    // --- IPipelineRunService context lookups ---
    // Launch context read out of a run's additional variables. Stays on the engine for now: the
    // launcher (palier 6) is its real home, and both the trigger coordinator and the step dispatcher
    // read it until then.
    internal static string? ResolveSourceCommit(IReadOnlyDictionary<string, string>? additionalVariables)
        => additionalVariables is not null
            && additionalVariables.TryGetValue(SourceCommitVariable, out var commit)
            && IsGitCommitHash(commit)
                ? commit
                : null;

    /// <summary>The revision a run's definition, templates and <c>.pipeline/configs</c> files come from:
    /// the definition's own when the workspace is checked out from another repository, else the run's.</summary>
    internal static string? ResolveDefinitionCommit(PipelineRun run) =>
        DeserializeResolvedVariables(run.AdditionalVariablesJson).TryGetValue(DefinitionCommitVariable, out var commit)
        && IsGitCommitHash(commit)
            ? commit
            : run.CommitHash;

    /// <summary>The branch a run's definition was read on, under the same rule.</summary>
    internal static string? ResolveDefinitionBranch(PipelineRun run) =>
        DeserializeResolvedVariables(run.AdditionalVariablesJson).TryGetValue(DefinitionBranchVariable, out var branch)
        && !string.IsNullOrWhiteSpace(branch)
            ? branch
            : run.BranchName;

    internal static string? ResolveRunBranch(IReadOnlyDictionary<string, string>? additionalVariables)
    {
        if (additionalVariables is not null
            && additionalVariables.TryGetValue(SourceBranchVariable, out var selectedBranch)
            && !string.IsNullOrWhiteSpace(selectedBranch))
            return selectedBranch;

        if (additionalVariables is null
            || !additionalVariables.TryGetValue("WEBHOOK_REF", out var reference)
            || !reference.StartsWith("refs/heads/", StringComparison.Ordinal)) return null;

        return reference["refs/heads/".Length..];
    }

    public Task<int?> GetPipelineIdForRunAsync(int runId, CancellationToken ct = default)
        => repo.GetPipelineIdForRunAsync(runId, ct);

    public async Task<(int PipelineId, int? ProjectId)?> GetRunPipelineContextAsync(int runId, CancellationToken ct = default)
    {
        var dto = await GetRunAsync(runId, ct).ConfigureAwait(false);
        if (dto is null) return null;
        return (dto.PipelineId, dto.ProjectId);
    }

    public Task<int?> GetPipelineIdForApprovalAsync(int approvalId, CancellationToken ct = default)
        => repo.GetPipelineIdForApprovalAsync(approvalId, ct);

    public Task<bool> IsServerAssignedToRunAsync(int runId, int serverId, CancellationToken ct = default)
        => repo.IsServerAssignedToRunAsync(runId, serverId, ct);

    public async Task<bool> HasRunToEndFailureAsync(int runId, CancellationToken ct = default) =>
        (await repo.GetContinuableFailedStepsInRunAsync(runId, ct).ConfigureAwait(false) ?? [])
            .Any(step => step.ContinueOnErrorForced);

    public async Task<List<string>> GetActiveWorkspaceSlotsAsync(CancellationToken ct = default)
    {
        // The slot, not the run id, is what an agent can match against a directory on disk. Deriving
        // it here keeps the hash in one place instead of asking every agent to reimplement it.
        var runIds = await repo.GetAllActiveRunIdsAsync(ct).ConfigureAwait(false);
        return [.. runIds.Select(PipelineCommandBuilder.GetWorkspaceSlot)];
    }
}
