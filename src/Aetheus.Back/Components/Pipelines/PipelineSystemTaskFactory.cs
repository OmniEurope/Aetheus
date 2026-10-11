// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Builds the two stages Aetheus injects around a run rather than reading them from the YAML:
/// System:Prepare (clone the pinned commit into the workspace) and System:Cleanup (purge it).
/// </summary>
public interface IPipelineSystemTaskFactory
{
    /// <summary>Creates the agent tasks for a set of injected system steps.</summary>
    /// <returns><c>false</c> when no runner could take them, or when cleanup was queued durably for an
    /// offline affinity runner - in both cases the caller must not treat the stage as dispatched.</returns>
    Task<bool> CreateSystemTasksAsync(
        int runId, List<PipelineStepRun> systemSteps, Dictionary<string, string> resolvedVars,
        PipelineYamlDefinition definition, int? organizationId, int? projectId, List<string> completedStages,
        CancellationToken ct);

    /// <summary>Dispatches the pending System:Cleanup stage of a run, if it has one left.</summary>
    /// <returns><c>true</c> when cleanup is now running or durably queued, so the caller must stop.</returns>
    Task<bool> DispatchCleanupIfPendingAsync(int runId, CancellationToken ct);
}

/// <summary>
/// The System:Prepare / System:Cleanup factory, extracted from <see cref="PipelineRunService"/>. It is
/// a leaf: it picks a runner (honouring run affinity so cleanup lands where prepare ran), enforces the
/// deployment-target and container-isolation contracts fail-closed, and writes tasks. It never advances
/// a stage and never decides the fate of a run - it reports whether it dispatched, and the engine
/// decides (ADR-041).
/// </summary>
public sealed class PipelineSystemTaskFactory(
    IPipelineRepository repo,
    IPipelineDispatchServerResolver dispatchServers,
    IPipelineRunFinalizer finalizer,
    IPipelineRunDefinitionParser definitions,
    IPipelineVariableResolver variableResolver,
    ISecretMaskingService secretMasking,
    IAuditService audit,
    IEncryptionService encryption,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<PipelineSystemTaskFactory> logger) : IPipelineSystemTaskFactory
{
    public async Task<bool> CreateSystemTasksAsync(
        int runId, List<PipelineStepRun> systemSteps, Dictionary<string, string> resolvedVars,
        PipelineYamlDefinition definition, int? organizationId, int? projectId, List<string> completedStages,
        CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (run?.Pipeline is null) return false;

        // The canonical clone URL is snapshotted on the run together with the immutable commit.
        // Project.RepositoryUrl is a legacy fallback and can legitimately be empty for repositories
        // hosted by GitLight, even though this run has a valid source workspace.
        var hasRepo = !string.IsNullOrWhiteSpace(run.RepositoryUrl);

        // Use run affinity if available - cleanup should run on the same server as prepare
        var affinityServerId = await repo.GetRunAffinityServerIdAsync(runId, ct).ConfigureAwait(false);
        var isCleanup = systemSteps.All(step => step.StageName == PipelineRunService.SystemCleanupStage);
        var deferredCleanup = false;
        Server? server = null;
        if (affinityServerId is not null)
            server = await repo.FindOnlineServerByIdAsync(affinityServerId.Value, ct: ct).ConfigureAwait(false);
        if (isCleanup && affinityServerId is not null && server is null)
        {
            server = await repo.FindServerByIdAsync(affinityServerId.Value, ct).ConfigureAwait(false);
            if (server is null)
                return false;
            deferredCleanup = true;
            logger.LogWarning(
                "Run {RunId} cleanup is queued durably for its offline affinity runner {ServerId}.",
                runId, affinityServerId.Value);
        }
        if (server is null)
        {
            var firstStageDef = YamlParsingHelper.FlattenJobs(definition).FirstOrDefault();
            server = firstStageDef is not null
                ? await dispatchServers.ResolveServerForTargetAsync(firstStageDef, resolvedVars, organizationId, false, projectId, ct).ConfigureAwait(false)
                : await repo.FindAnyOnlineRunnerAsync(organizationId, ct: ct).ConfigureAwait(false);
        }
        if (server is null) return false;

        var targetViolation = PipelineDeploymentTargetGuard.ValidateServer(resolvedVars, server);
        if (targetViolation is not null)
        {
            await finalizer.FailRunWithUnmatchedStagesAsync(runId, [targetViolation], systemSteps, ct).ConfigureAwait(false);
            return false;
        }

        var isWindows = OsTypeHelper.IsWindows(server.OsType, server.OsDescription);
        // Phase 2: the whole run executes in containers when isolation is set at the run level.
        // Container execution is Linux-only - the system scripts use the Linux variants and the
        // workspace is the in-container mount point (/w); the agent bind-mounts the run's host dir.
        var runIsolation = definition.Isolation;
        var isContainer = runIsolation?.IsContainer == true;
        var useWindows = isWindows && !isContainer;

        // Fail-closed: never dispatch a container-isolated system task (prepare/clone/cleanup) to a
        // runner that can't honor the run-level isolation contract. Without this the run would hard-
        // fail late at the agent instead of being blocked cleanly here with a readable reason.
        var isolationViolation = CheckRunIsolationPolicy(server, runIsolation);
        if (isolationViolation is not null)
        {
            await finalizer.FailRunWithUnmatchedStagesAsync(runId, [isolationViolation], systemSteps, ct).ConfigureAwait(false);
            return false;
        }

        foreach (var step in systemSteps)
            CreateSystemTask(
                runId, run, server, step, resolvedVars, runIsolation, hasRepo,
                isWindows, isContainer, useWindows, deferredCleanup);

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        if (deferredCleanup)
        {
            await audit.LogAsync(
                "QueuedDeferredPipelineCleanup",
                "PipelineRun",
                runId,
                $"Runner {server.Id}",
                ct).ConfigureAwait(false);
        }
        return !deferredCleanup;
    }

    private void CreateSystemTask(
        int runId,
        PipelineRun run,
        Server server,
        PipelineStepRun step,
        Dictionary<string, string> resolvedVars,
        PipelineIsolationDefinition? runIsolation,
        bool hasRepo,
        bool isWindows,
        bool isContainer,
        bool useWindows,
        bool deferredCleanup)
    {
        var isPrepare = step.StageName == PipelineRunService.SystemPrepareStage;
        step.ServerId = server.Id;
        var workspace = ResolveSystemWorkspace(runId, resolvedVars, isWindows, isContainer);
        resolvedVars["WORKSPACE"] = workspace;
        var command = BuildSystemCommand(workspace, isPrepare, hasRepo, useWindows);

        var taskVars = resolvedVars;
        if (isPrepare && hasRepo)
        {
            if (MirrorCloneUrl.IsCurrentInternalClone(configuration, run.RepositoryUrl))
                taskVars = AddCloneCredentials(runId, run, resolvedVars);
            else
            {
                taskVars = new Dictionary<string, string>(resolvedVars, StringComparer.OrdinalIgnoreCase);
                taskVars.Remove("GIT_USERNAME");
                taskVars.Remove("GIT_PASSWORD");
            }
        }

        var task = new ServerTask
        {
            ServerId = server.Id,
            Name = step.StepName,
            Command = command,
            PipelineRunId = runId,
            PipelineStepRunId = deferredCleanup ? null : step.Id,
            IsDeferredCleanup = deferredCleanup,
            EnvironmentVariables = TaskEnvProtection.Protect(
                encryption, deferredCleanup ? "{}" : JsonSerializer.Serialize(taskVars)),
            TimeoutSeconds = isPrepare
                ? PipelineCommandBuilder.PrepareTimeoutSeconds
                : PipelineCommandBuilder.CleanupTimeoutSeconds
        };
        foreach (var warning in ApplyContainerIsolation(task, runIsolation))
            logger.LogWarning("Run {RunId} step '{Step}': {Warning}", runId, step.StepName, warning);
        repo.TrackTask(task);
        if (deferredCleanup)
            finalizer.MarkStepsAs([step], TaskExecutionStatus.Failed);
        else
            MarkStepDispatched(step, task);
    }

    private static string ResolveSystemWorkspace(
        int runId, IReadOnlyDictionary<string, string> variables, bool isWindows, bool isContainer)
    {
        if (isContainer) return ContainerWorkspace;
        var workspace = variables.GetValueOrDefault("WORKSPACE", "");
        return isWindows || string.IsNullOrEmpty(workspace)
            ? PipelineCommandBuilder.GetDefaultWorkspace(runId, isWindows)
            : workspace;
    }

    private static string BuildSystemCommand(
        string workspace, bool isPrepare, bool hasRepo, bool useWindows)
    {
        if (!isPrepare)
            return useWindows
                ? PipelineCommandBuilder.BuildWindowsCleanupCommand(workspace)
                : PipelineCommandBuilder.BuildLinuxCleanupCommand(workspace);
        if (hasRepo)
            return useWindows
                ? PipelineCommandBuilder.BuildWindowsCloneCommand(workspace)
                : PipelineCommandBuilder.BuildLinuxCloneCommand(workspace);
        return useWindows
            ? PipelineCommandBuilder.BuildWindowsPrepareCommand(workspace)
            : PipelineCommandBuilder.BuildLinuxPrepareCommand(workspace);
    }

    private Dictionary<string, string> AddCloneCredentials(
        int runId,
        PipelineRun run,
        Dictionary<string, string> resolvedVars)
    {
        if (run.Pipeline?.ProjectId is not { } projectId)
            throw new InvalidOperationException(
                $"Pipeline run {runId} has a repository snapshot but no owning project.");
        var (gitUser, gitPass) = GitRunCloneToken.Mint(
            configuration, runId, projectId,
            timeProvider.GetUtcNow().UtcDateTime, GitRunCloneToken.DefaultTtl);
        secretMasking.RegisterRuntimeSecret(runId, gitPass);
        return new Dictionary<string, string>(resolvedVars, StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_USERNAME"] = gitUser,
            ["GIT_PASSWORD"] = gitPass
        };
    }

    // In-container mount point for the run workspace. The agent bind-mounts the run's host directory
    // here, so every container-isolated step sees the cloned source at the same path regardless of
    // where the agent stores it on the host.
    /// <summary>The in-container mount point the agent bind-mounts the run's host directory onto.</summary>
    public const string ContainerWorkspace = "/w";

    public async Task<bool> DispatchCleanupIfPendingAsync(int runId, CancellationToken ct)
    {
        var pendingCleanup = await repo.GetPendingStepRunsAsync(runId, ct).ConfigureAwait(false);
        var cleanupSteps = pendingCleanup.Where(s => s.StageName == PipelineRunService.SystemCleanupStage).ToList();
        if (cleanupSteps.Count == 0) return false;

        // A failure can complete in parallel with an already-dispatched always()/failed() handler.
        // That handler is no longer Pending, so TryContinueWithFailureHandlersAsync cannot see it.
        // Never purge its workspace underneath it: its completion will re-enter this scheduler.
        if (await repo.HasAnyRunningStepInRunAsync(runId, ct).ConfigureAwait(false))
            return true;

        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (run?.Pipeline is null) return false;

        var definition = definitions.Parse(run);
        if (definition is null) return false;

        var resolvedVars = await variableResolver.ResolveFullVariablesForRunAsync(run, definition, ct).ConfigureAwait(false);
        if (await CreateSystemTasksAsync(runId, cleanupSteps, resolvedVars, definition, null, null, [], ct).ConfigureAwait(false))
            return true;

        if (!await finalizer.IsRunActiveAsync(runId, ct).ConfigureAwait(false)) return false;
        finalizer.MarkStepsAs(cleanupSteps, TaskExecutionStatus.Failed);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await repo.AppendRunWarningsAsync(runId,
            ["System cleanup is queued for the original affinity runner; its workspace requires reconciliation when that runner returns online."],
            ct).ConfigureAwait(false);
        return false;
    }
}
