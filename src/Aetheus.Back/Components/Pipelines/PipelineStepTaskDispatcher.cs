// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Turns one ready stage into agent tasks: fans it out across its matrix legs, resolves a runner per
/// leg, then dispatches each step according to its type.
/// </summary>
public interface IPipelineStepTaskDispatcher
{
    /// <summary>Creates the tasks for a stage's steps, across every matrix leg.</summary>
    /// <param name="launcher">Passed through to the trigger coordinator - see
    /// <see cref="IPipelineChildRunLauncher"/> for why it travels as a parameter.</param>
    /// <returns><c>true</c> when this stage ALREADY finalized the run (a fail-closed matrix-leg or
    /// isolation violation failed it here), so the caller must not re-fail it.</returns>
    Task<bool> CreateTasksForStageAsync(
        int runId, Server server, List<PipelineStepRun> stageSteps, PipelineStageDefinition stageDef,
        Dictionary<string, string> resolvedVars, List<string> completedStages, bool previousStageFailed,
        HashSet<string> secretKeys, int? organizationId, int? projectId,
        IPipelineChildRunLauncher launcher, CancellationToken ct);
}

/// <summary>
/// The step dispatcher, extracted from <see cref="PipelineRunService"/>. Every step type that is not a
/// plain agent command lands here - release, substitute, AI, and the extended host/deployment/analysis/
/// artifact/scanner families delegated to their factories - together with the matrix-leg fan-out that
/// decides which runner each leg goes to.
///
/// It builds and reports; it never advances a stage (ADR-041). The one exception is spelled out in its
/// return value: a fail-closed leg or isolation violation finalizes the run here, and the caller is told
/// so it does not fail it twice.
/// </summary>
public sealed class PipelineStepTaskDispatcher(
    IPipelineRepository repo,
    IPipelineDispatchServerResolver dispatchServers,
    IPipelineStepTaskBuilder taskBuilder,
    IPipelineRunFinalizer finalizer,
    IPipelineTriggerStepCoordinator triggerSteps,
    IPipelineBranchAdvanceStep branchAdvanceSteps,
    IPipelineAnalysisTaskFactory analysisTasks,
    IPipelineDotnetTestTaskFactory dotnetTestTasks,
    IPipelineFunctionTaskFactory functionTasks,
    IPipelineGateStatusTaskFactory gateStatusTasks,
    IPipelineDeploymentTaskFactory deploymentTasks,
    IPipelineHostOperationTaskFactory hostTasks,
    IPipelineArtifactTaskFactory artifactTasks,
    IPipelineScannerTaskFactory scannerTasks,
    IAiTaskService aiTaskService,
    IEncryptionService encryption,
    IAppDeployEnvProvider appDeployEnv,
    IConfiguration configuration,
    ISecretMaskingService secretMasking,
    TimeProvider timeProvider,
    ILogger<PipelineStepTaskDispatcher> logger) : IPipelineStepTaskDispatcher
{
    private sealed class StageTaskCreationState
    {
        public PipelineRun? ReleaseRun { get; set; }
        public int? ReleaseProjectId { get; set; }
        public string? ReleasePattern { get; set; }
        public bool ReleasePatternFetched { get; set; }
        public int? PipelineAiProfileId { get; set; }
        public bool PipelineAiProfileFetched { get; set; }
    }

    // Returns true when this stage ALREADY finalized the run (a fail-closed matrix-leg / isolation
    // violation calls FailRunWithUnmatchedStagesAsync itself), so the caller must not re-fail it.
    public async Task<bool> CreateTasksForStageAsync(
        int runId, Server server, List<PipelineStepRun> stageSteps, PipelineStageDefinition stageDef,
        Dictionary<string, string> resolvedVars, List<string> completedStages, bool previousStageFailed,
        HashSet<string> secretKeys, int? organizationId, int? projectId,
        IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var stageIsContainer = stageDef.Isolation?.IsContainer == true;
        var stageVars = await BuildStageVariablesAsync(
            runId, resolvedVars, stageDef, stageIsContainer, ct).ConfigureAwait(false);
        var stepsByLeg = stageSteps.GroupBy(s => s.MatrixLeg ?? string.Empty);
        var matrixLegs = ExpandMatrix(stageDef.Matrix);
        var creationState = new StageTaskCreationState();
        foreach (var legGroup in stepsByLeg)
        {
            var legVars = ResolveLegVariables(stageVars, legGroup.Key, matrixLegs);
            var legSteps = legGroup.ToList();
            var resolution = await ResolveAndValidateLegAsync(
                runId, stageDef, legGroup.Key, legVars, server, stageIsContainer, organizationId, projectId, ct)
                .ConfigureAwait(false);
            if (resolution.Error is not null)
            {
                await finalizer.FailRunWithUnmatchedStagesAsync(runId, [resolution.Error], legSteps, ct).ConfigureAwait(false);
                return true;
            }
            if (await DispatchLegStepsAsync(
                    runId, resolution.Server!, legSteps, stageDef, legVars, completedStages,
                    previousStageFailed, stageIsContainer, secretKeys, organizationId, creationState, launcher, ct)
                .ConfigureAwait(false)) return true;
        }
        return false; // the run was not finalized by this stage
    }

    private async Task<Dictionary<string, string>> BuildStageVariablesAsync(
        int runId,
        Dictionary<string, string> resolvedVars,
        PipelineStageDefinition stageDef,
        bool stageIsContainer,
        CancellationToken ct)
    {
        var stageVars = new Dictionary<string, string>(resolvedVars, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in stageDef.Variables) stageVars[key] = value;
        if (!string.IsNullOrWhiteSpace(stageDef.ExecutionRole))
            stageVars["AETHEUS_EXECUTION_ROLE"] = stageDef.ExecutionRole.Trim().ToLowerInvariant();
        if (string.Equals(stageDef.ExecutionRole, "deploy", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(configuration["AppMonitoring:IngestBaseUrl"]))
            await AddDeployObservabilityVariablesAsync(runId, stageVars, ct).ConfigureAwait(false);
        if (stageIsContainer)
        {
            stageVars[PipelineRunService.HostWorkspaceVariable] = PipelineCommandBuilder.GetDefaultWorkspace(runId, isWindows: false);
            stageVars["WORKSPACE"] = PipelineSystemTaskFactory.ContainerWorkspace;
        }
        return stageVars;
    }

    internal async Task AddDeployObservabilityVariablesAsync(
        int runId, IDictionary<string, string> stageVars, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var projectId = run?.Pipeline is null
            ? null
            : await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        if (projectId is null) return;
        var observabilityEnv = await appDeployEnv
            .GetDeployEnvAsync(projectId.Value, environmentId: null, runId, ct).ConfigureAwait(false);
        foreach (var (key, value) in observabilityEnv) stageVars[key] = value;
    }

    private async Task<MatrixLegResolution> ResolveAndValidateLegAsync(
        int runId,
        PipelineStageDefinition stageDef,
        string matrixLeg,
        Dictionary<string, string> legVars,
        Server server,
        bool stageIsContainer,
        int? organizationId,
        int? projectId,
        CancellationToken ct)
    {
        var legServer = await ResolveMatrixLegServerAsync(
            stageDef, legVars, server, stageIsContainer, organizationId, projectId, ct).ConfigureAwait(false);
        if (legServer is null)
            return MatrixLegResolution.Failed(
                $"Stage '{stageDef.Name}' matrix leg '{matrixLeg}': no online runner available for os '{legVars.GetValueOrDefault("os")}'.");
        var isolationViolation = CheckIsolationPolicy(stageDef.Name, stageDef, legServer);
        if (isolationViolation is not null) return MatrixLegResolution.Failed(isolationViolation);
        NormalizeLegWorkspace(runId, stageIsContainer, legServer, legVars);
        PipelineVariableResolver.InjectStageSystemVariables(legVars, stageDef.Name, legServer);
        var targetViolation = PipelineDeploymentTargetGuard.ValidateServer(legVars, legServer);
        return targetViolation is null
            ? MatrixLegResolution.Succeeded(legServer)
            : MatrixLegResolution.Failed(targetViolation);
    }

    /// <summary>
    /// A Windows runner cannot use a POSIX workspace path. Internal rather than private so its five
    /// branches are unit-tested directly instead of through a full stage dispatch.
    /// </summary>
    internal static void NormalizeLegWorkspace(
        int runId, bool stageIsContainer, Server server, Dictionary<string, string> legVars)
    {
        if (stageIsContainer
            || !OsTypeHelper.IsWindows(server.OsType, server.OsDescription)
            || !legVars.TryGetValue("WORKSPACE", out var workspace)
            || workspace.Contains(":\\")) return;
        // A missing BUILD_BUILDID means the run id IS the identity; it must never default to "0",
        // which parses and silently sent the leg into the workspace of run 0.
        var buildId = legVars.TryGetValue("BUILD_BUILDID", out var runIdText)
                      && int.TryParse(runIdText, out var parsedRunId)
            ? parsedRunId
            : runId;
        legVars["WORKSPACE"] = PipelineCommandBuilder.GetDefaultWorkspace(buildId, isWindows: true);
    }

    private async Task<bool> DispatchLegStepsAsync(
        int runId,
        Server server,
        IReadOnlyCollection<PipelineStepRun> steps,
        PipelineStageDefinition stageDef,
        Dictionary<string, string> legVars,
        List<string> completedStages,
        bool previousStageFailed,
        bool stageIsContainer,
        HashSet<string> secretKeys,
        int? organizationId,
        StageTaskCreationState creationState,
        IPipelineChildRunLauncher launcher,
        CancellationToken ct)
    {
        foreach (var stepRun in steps)
        {
            var baseStepName = stepRun.MatrixLeg is not null
                ? stepRun.StepName.Replace($" [{stepRun.MatrixLeg}]", string.Empty)
                : stepRun.StepName;
            var stepDef = stageDef.Steps.FirstOrDefault(step => step.Name == baseStepName);
            if (stepDef is null)
            {
                FailMissingStepDefinition(runId, stepRun, stageDef.Name);
                continue;
            }
            if (!EvaluateCondition(stepDef.Condition, legVars, completedStages, previousStageFailed))
            {
                MarkStepConditionNotMet(runId, stepRun, stepDef, legVars, secretKeys);
                continue;
            }
            var dispatchResult = await DispatchStepAsync(
                runId, server, stepRun, stepDef, legVars, stageDef, stageIsContainer,
                secretKeys, organizationId, creationState, launcher, ct).ConfigureAwait(false);
            if (dispatchResult == StepDispatchResult.RunFinalized) return true;
        }
        return false;
    }

    private void FailMissingStepDefinition(int runId, PipelineStepRun stepRun, string stageName)
    {
        logger.LogWarning(
            "Run {RunId}: step '{Step}' in stage '{Stage}' has no matching YAML definition; failing it to avoid a stranded run",
            runId, stepRun.StepName, stageName);
        MarkSystemStepFailed(
            stepRun,
            TaskFailureCodes.InfrastructureMismatch,
            $"Step '{stepRun.StepName}' has no matching definition in stage '{stageName}'.",
            timeProvider.GetUtcNow().UtcDateTime);
    }

    private void MarkStepConditionNotMet(
        int runId,
        PipelineStepRun stepRun,
        PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars,
        HashSet<string> secretKeys)
    {
        stepRun.Status = TaskExecutionStatus.Cancelled;
        stepRun.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        stepRun.SkippedCondition = PipelineConditionEvidence.CompactCondition(stepDef.Condition!);
        var capturedVariables = PipelineConditionEvidence.CaptureVariables(
            stepDef.Condition!, legVars, secretKeys, secretMasking.GetRuntimeSecretValues(runId));
        stepRun.SkippedConditionVariablesJson = PipelineConditionEvidence.SerializeVariables(capturedVariables);
    }

    private sealed record MatrixLegResolution(Server? Server, string? Error)
    {
        public static MatrixLegResolution Succeeded(Server server) => new(server, null);
        public static MatrixLegResolution Failed(string error) => new(null, error);
    }

    private async Task<StepDispatchResult> DispatchStepAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef, bool stageIsContainer,
        HashSet<string> secretKeys, int? organizationId, StageTaskCreationState creationState,
        IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var type = stepDef.Type?.ToLowerInvariant();
        switch (type)
        {
            case "substitute":
                CreateSubstituteTask(runId, legServer, stepRun, stepDef, legVars);
                return StepDispatchResult.Handled;
            case "release":
                await CreateReleaseTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, creationState, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case "ai":
                return await CreateAiTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, stageDef, organizationId, creationState, ct)
                    .ConfigureAwait(false);
            case "scanner":
                return await scannerTasks.CreateScannerTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, stageDef, stageIsContainer, ct)
                    .ConfigureAwait(false);
            case "publish-observability":
                analysisTasks.CreateObservabilityTask(runId, legServer, stepRun, stepDef, legVars, secretKeys);
                return StepDispatchResult.Handled;
            case "coverage":
                analysisTasks.CreateCoverageTask(runId, legServer, stepRun, stepDef, legVars, stageDef);
                return StepDispatchResult.Handled;
        }
        var (handled, factoryError) = CreateFactoryTask(type, runId, legServer, stepRun, stepDef, legVars, stageDef);
        if (handled)
            return await FailRunOnFactoryErrorAsync(runId, stepRun, factoryError, ct).ConfigureAwait(false);
        return await DispatchExtendedStepAsync(
            type, runId, legServer, stepRun, stepDef, legVars, stageDef, stageIsContainer,
            secretKeys, launcher, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The step types whose factory validates and builds the task, returning the refusal it found:
    /// <c>Handled</c> false for any other type. Finalizing a run on a refusal stays with the engine.
    /// </summary>
    private (bool Handled, string? Error) CreateFactoryTask(
        string? type, int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef) => type switch
        {
            "analysis-gate" => (true, analysisTasks.CreateAnalysisGateTask(runId, legServer, stepRun, stepDef, legVars, stageDef)),
            "dotnet-test" => (true, dotnetTestTasks.CreateDotnetTestTask(runId, legServer, stepRun, stepDef, legVars, stageDef)),
            "gate-status" => (true, gateStatusTasks.CreateGateStatusTask(runId, legServer, stepRun, stepDef, legVars, stageDef)),
            PipelineFunctionCatalog.StepType => (true, functionTasks.CreateFunctionTask(runId, legServer, stepRun, stepDef, legVars, stageDef)),
            _ => (false, null)
        };

    /// <summary>A factory that refused its step returns the reason; the run fails with it, here.</summary>
    private async Task<StepDispatchResult> FailRunOnFactoryErrorAsync(
        int runId, PipelineStepRun stepRun, string? factoryError, CancellationToken ct)
    {
        if (factoryError is null) return StepDispatchResult.Handled;
        await finalizer.FailRunWithUnmatchedStagesAsync(runId, [factoryError], [stepRun], ct).ConfigureAwait(false);
        return StepDispatchResult.RunFinalized;
    }

    private async Task<StepDispatchResult> DispatchExtendedStepAsync(
        string? type, int runId, Server legServer, PipelineStepRun stepRun,
        PipelineStepDefinition stepDef, Dictionary<string, string> legVars,
        PipelineStageDefinition stageDef, bool stageIsContainer, HashSet<string> secretKeys,
        IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        switch (type)
        {
            case "lint":
                analysisTasks.CreateLintTask(runId, legServer, stepRun, stepDef, legVars, stageDef);
                return StepDispatchResult.Handled;
            case "complexity":
                analysisTasks.CreateComplexityTask(runId, legServer, stepRun, stepDef, legVars, stageDef);
                return StepDispatchResult.Handled;
            case "mutation":
                analysisTasks.CreateMutationTask(runId, legServer, stepRun, stepDef, legVars, stageDef);
                return StepDispatchResult.Handled;
            case "artifacts":
                analysisTasks.CreateCollectArtifactsTask(runId, legServer, stepRun, stepDef, legVars, stageDef);
                return StepDispatchResult.Handled;
            case "restore-artifacts":
                await artifactTasks.CreateRestoreArtifactsTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case "restore-backup":
                await artifactTasks.CreateBackupRestoreTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case "deploy":
                await deploymentTasks.CreateDeployTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case "apache-proxy":
                await hostTasks.CreateApacheProxyTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case "apache-config":
                await hostTasks.CreateApacheConfigTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case "certbot":
                await hostTasks.CreateCertbotTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case "smoke":
                await hostTasks.CreateSmokeTaskAsync(
                    runId, legServer, stepRun, stepDef, legVars, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case string blueGreenType when blueGreenType.StartsWith("bluegreen-", StringComparison.Ordinal):
                await hostTasks.CreateBlueGreenTaskAsync(
                    type, runId, legServer, stepRun, stepDef, legVars, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case "trigger":
                await triggerSteps.CreateTriggerStepAsync(
                    runId, stepRun, stepDef, legVars, secretKeys, launcher, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            case "advance-branch":
                await branchAdvanceSteps.ExecuteAsync(runId, stepRun, stepDef, legVars, ct).ConfigureAwait(false);
                return StepDispatchResult.Handled;
            default:
                return DispatchCommandStep(
                    runId, legServer, stepRun, stepDef, legVars, stageDef, stageIsContainer, secretKeys);
        }
    }

    private StepDispatchResult DispatchCommandStep(
        int runId, Server legServer, PipelineStepRun stepRun,
        PipelineStepDefinition stepDef, Dictionary<string, string> legVars,
        PipelineStageDefinition stageDef, bool stageIsContainer, HashSet<string> secretKeys)
    {
        // A type written for a newer backend validates with a warning; with no command of its own
        // it would reach the agent empty, so it fails here, by name.
        if (!string.IsNullOrWhiteSpace(stepDef.Type)
            && !PipelineDefinitionValidator.IsKnownStepType(stepDef.Type)
            && string.IsNullOrWhiteSpace(stepDef.Shell) && !stepDef.Checkout)
        {
            PipelineRunHelpers.MarkSystemStepFailed(stepRun, TaskFailureCodes.ToolError,
                $"Unknown step type '{stepDef.Type}': this backend does not know it (a newer one may).",
                timeProvider.GetUtcNow().UtcDateTime);
            return StepDispatchResult.Handled;
        }
        taskBuilder.CreateCommandTask(
            runId, legServer, stepRun, stepDef, legVars, stageDef, stageIsContainer, secretKeys);
        return StepDispatchResult.Handled;
    }

    private void CreateSubstituteTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars)
    {
        var variables = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_RUN_ID"] = runId.ToString(),
            ["AETHEUS_WORKING_DIR"] = legVars.GetValueOrDefault("WORKSPACE", "")
        };
        stepRun.ServerId = legServer.Id;
        var task = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = JsonSerializer.Serialize(stepDef.TargetFiles),
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(variables)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = OperationKind.PipelineSubstituteVariables
        };
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
    }

    private async Task CreateReleaseTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, StageTaskCreationState state, CancellationToken ct)
    {
        if (state.ReleaseProjectId is null)
        {
            state.ReleaseRun = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
            state.ReleaseProjectId = state.ReleaseRun?.Pipeline is null
                ? 0
                : await repo.GetPipelineProjectIdAsync(state.ReleaseRun.Pipeline, ct).ConfigureAwait(false) ?? 0;
        }
        var releaseVars = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_PROJECT_ID"] = state.ReleaseProjectId.Value.ToString(),
            ["AETHEUS_RUN_ID"] = runId.ToString(),
            ["AETHEUS_CHANGELOG"] = stepDef.Changelog.ToString().ToLowerInvariant(),
            ["AETHEUS_RELEASE_DEPLOYED"] = stepDef.Deployed.ToString().ToLowerInvariant(),
            ["AETHEUS_WORKING_DIR"] = legVars.GetValueOrDefault("WORKSPACE", "")
        };
        if (IsGitCommitHash(state.ReleaseRun?.CommitHash))
            releaseVars["AETHEUS_RELEASE_COMMIT"] = state.ReleaseRun!.CommitHash!;
        if (!string.IsNullOrWhiteSpace(state.ReleaseRun?.BranchName))
            releaseVars["AETHEUS_RELEASE_BRANCH"] = state.ReleaseRun!.BranchName;

        if (!string.IsNullOrWhiteSpace(stepDef.ArtifactSourcePipeline))
        {
            var (artifact, error) = await artifactTasks.ResolveArtifactSourceAsync(runId, legVars, stepDef, ct)
                .ConfigureAwait(false);
            if (artifact is null)
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                stepRun.ServerId = legServer.Id;
                MarkSystemStepFailed(stepRun, TaskFailureCodes.ToolError,
                    error ?? "The release artifact could not be resolved.", now);
                await repo.AppendRunWarningsAsync(
                    runId, [$"Release step '{stepRun.StepName}': {error}"], ct).ConfigureAwait(false);
                return;
            }
            releaseVars["AETHEUS_RELEASE_ARTIFACT_RUN_ID"] = artifact.PipelineRunId.ToString();
        }
        if (!state.ReleasePatternFetched)
        {
            if (state.ReleaseProjectId.Value > 0)
                state.ReleasePattern = await repo.GetProjectReleasePatternAsync(
                    state.ReleaseProjectId.Value, ct).ConfigureAwait(false);
            state.ReleasePatternFetched = true;
        }
        var versionPattern = stepDef.Version
            ?? (string.IsNullOrWhiteSpace(state.ReleasePattern) ? "1.0.$(BUILD_BUILDID)" : state.ReleasePattern);
        var version = SubstituteVariables(versionPattern, legVars);
        stepRun.ServerId = legServer.Id;
        var task = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = version,
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(releaseVars)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = OperationKind.PipelineCreateRelease
        };
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
    }

    private async Task<StepDispatchResult> CreateAiTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef, int? organizationId,
        StageTaskCreationState state, CancellationToken ct)
    {
        // The gate's VERDICT convention and the findings format are in the agent's system prompt
        // (AiPromptAssembler), the same for every provider.
        var inlinePrompt = SubstituteVariables(stepDef.Prompt ?? string.Empty, legVars);
        // The step's profile name, then the pipeline's assigned profile, then the organization's most
        // preferred usable one (AiProfileResolver); the choice is written to the step log by the agent.
        var execution = organizationId is null
            ? null
            : await aiTaskService.BuildPipelineExecutionAsync(
                stepDef.Profile, await GetPipelineAiProfileIdAsync(runId, state, ct).ConfigureAwait(false),
                organizationId.Value, inlinePrompt, stepDef.Gate, runId,
                legVars.GetValueOrDefault("WORKSPACE", string.Empty), ct).ConfigureAwait(false);
        var spec = execution?.Spec;
        if (spec is null)
        {
            var reason = execution is null
                ? $"Step '{stepDef.Name}': the pipeline belongs to no organization, so no AI runner profile can be resolved."
                : $"Step '{stepDef.Name}': {execution.Problem ?? "no AI runner profile could be resolved."}";
            // An advisory step informs and never decides: its own setup failing is recorded on the step,
            // and the run carries on as if it had not been there.
            if (stepDef.Advisory)
            {
                stepRun.ServerId = legServer.Id;
                MarkSystemStepFailed(stepRun, TaskFailureCodes.InfrastructureMismatch,
                    $"Advisory step not run: {reason}", timeProvider.GetUtcNow().UtcDateTime);
                return StepDispatchResult.Handled;
            }
            await finalizer.FailRunWithUnmatchedStagesAsync(runId, [reason], [stepRun], ct).ConfigureAwait(false);
            return StepDispatchResult.RunFinalized;
        }
        // Never silent: a profile picked by fallback is also recorded on the run, so it shows even when the
        // agent that runs the step predates the step-log line.
        if (execution!.IsFallback)
            await repo.AppendRunWarningsAsync(
                runId, [$"AI step '{stepDef.Name}': {execution.Resolution}"], ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stepDef.PromptFile))
            spec.EnvironmentVariables["AETHEUS_AI_PROMPT_FILE"] = stepDef.PromptFile;
        if (stepDef.ContextPaths.Count > 0)
            spec.EnvironmentVariables["AETHEUS_AI_CONTEXT_PATHS_JSON"] =
                JsonSerializer.Serialize(stepDef.ContextPaths);
        AddAiPublicationVariables(spec.EnvironmentVariables, runId, stageDef.Name, stepDef);
        if (!string.IsNullOrWhiteSpace(stepDef.GitRange))
            spec.EnvironmentVariables["AETHEUS_AI_GIT_RANGE"] = SubstituteVariables(stepDef.GitRange, legVars).Trim();

        stepRun.ServerId = legServer.Id;
        var task = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = spec.Target,
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(
                encryption, JsonSerializer.Serialize(spec.EnvironmentVariables)),
            TimeoutSeconds = Math.Min(stepDef.TimeoutSeconds, spec.TimeoutSeconds),
            Operation = OperationKind.AiRun
        };
        foreach (var warning in ApplyContainerIsolation(task, stageDef.Isolation))
            logger.LogWarning("Run {RunId} step '{Step}': {Warning}", runId, stepRun.StepName, warning);
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
        return StepDispatchResult.Handled;
    }

    /// <summary>The profile assigned to the run's pipeline, read once per stage (null: automatic).</summary>
    private async Task<int?> GetPipelineAiProfileIdAsync(int runId, StageTaskCreationState state, CancellationToken ct)
    {
        if (!state.PipelineAiProfileFetched)
        {
            state.PipelineAiProfileId = (await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false))
                ?.Pipeline?.AiRunnerProfileId;
            state.PipelineAiProfileFetched = true;
        }
        return state.PipelineAiProfileId;
    }

    /// <summary>What the agent needs to honour an AI step's advisory switch and to publish its
    /// findings under the run and stage.</summary>
    internal static void AddAiPublicationVariables(
        Dictionary<string, string> environment, int runId, string stageName, PipelineStepDefinition stepDef)
    {
        if (stepDef.Advisory) environment["AETHEUS_AI_ADVISORY"] = "true";
        if (!stepDef.Findings) return;
        environment["AETHEUS_AI_FINDINGS"] = "true";
        if (stepDef.FindingsPlaceholder) environment["AETHEUS_AI_FINDINGS_PLACEHOLDER"] = "true";
        environment["AETHEUS_RUN_ID"] = runId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["AETHEUS_STAGE_NAME"] = stageName;
    }

    // S-FEAT-21: pick the runner for a matrix leg. An `os` matrix axis targets an OS-appropriate
    // agent so a single definition fans out across platforms (cross-OS build matrix). Every other
    // case reuses the stage's already isolation-checked server, so behavior is unchanged without an
    // os axis. The leg's os stays within the stage's selector + organization, so it never widens the
    // F-EXEC-1 candidate set the pre-run Server.Admin gate authorized.
    private async Task<Server?> ResolveMatrixLegServerAsync(
        PipelineStageDefinition stageDef, Dictionary<string, string> legVars, Server stageServer,
        bool stageIsContainer, int? organizationId, int? projectId, CancellationToken ct)
    {
        // Container stages run every leg on the single Docker-capable runner already vetted upstream.
        if (stageIsContainer) return stageServer;
        if (!legVars.TryGetValue("os", out var legOs) || string.IsNullOrWhiteSpace(legOs))
            return stageServer;

        var requested = OsTypeHelper.Parse(legOs);
        if (requested == OsType.Unknown) return stageServer;

        // No extra lookup when the leg's OS already matches the stage's resolved runner.
        var stageIsWindows = OsTypeHelper.IsWindows(stageServer.OsType, stageServer.OsDescription);
        if (stageIsWindows == (requested == OsType.Windows)) return stageServer;

        return await dispatchServers.ResolveServerForTargetAsync(
            stageDef with { Os = legOs }, legVars, organizationId, false, projectId, ct).ConfigureAwait(false);
    }
}
