// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;


/// <summary>
/// Owns everything a `type: trigger` step does: resolve the target pipeline inside the project (
/// materializing it from the run's immutable revision when it is not registered yet), refuse cycles and
/// over-deep chains, decide whether an earlier checkpoint can be adopted instead of running, and
/// otherwise start the child and leave the step waiting on it.
/// </summary>
public interface IPipelineTriggerStepCoordinator
{
    /// <summary>Executes a `type: trigger` step: completes it from a checkpoint, starts a child run, or
    /// fails it with a readable reason.</summary>
    Task CreateTriggerStepAsync(
        int runId, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, IReadOnlySet<string> secretKeys,
        IPipelineChildRunLauncher launcher, CancellationToken ct);

    /// <summary>Starts a downstream run declared by an upstream pipeline's <c>on_success:</c>.</summary>
    Task<PipelineChainedRunOutcome> TriggerChainedRunAsync(
        int childPipelineId, Dictionary<string, string>? upstreamVariables,
        IPipelineChildRunLauncher launcher, CancellationToken ct);
}

/// <summary>
/// The trigger-step coordinator, extracted from <see cref="PipelineRunService"/>. Two invariants survive
/// the move unchanged: the child run is persisted before its parent link, so a backend interruption
/// replays the SAME child (idempotency key <c>trigger-step:{parent}:{step}</c>) instead of leaking a new
/// orphan on every recovery sweep; and the orchestration chain is refused past
/// <c>MaxTriggerChainDepth</c> or on any cycle in the lineage.
/// </summary>
public sealed class PipelineTriggerStepCoordinator(
    IPipelineRepository repo,
    IPipelineGitService pipelineGit,
    IPipelineRunPreparationService preparations,
    IPipelineRunParameterResolver parameterResolver,
    IPipelineCheckpointReuseService checkpoints,
    IResourceAuthorizationService authz,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<PipelineTriggerStepCoordinator> logger) : IPipelineTriggerStepCoordinator
{
    private const int MaxTriggerChainDepth = 10;

    internal static string BuildTriggerStepIdempotencyKey(int parentRunId, int stepRunId) =>
        $"trigger-step:{parentRunId}:{stepRunId}";

    // Trigger step: resolve the named pipeline (same project), guard against cycles, start a child run
    // forwarding UPSTREAM_* context, and leave THIS step Running while linking it to the child run id.
    // The step is NOT dispatched as an agent task - PipelineRunCompletedTriggerHandler completes it when
    // the child run finishes. Any resolution/cycle failure fails the step honestly (never a fake green).
    public async Task CreateTriggerStepAsync(
        int runId, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars,
        IReadOnlySet<string> secretKeys,
        IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Neighbour of BACK-022: what this step hands the child is persisted with the child run
        // (AdditionalVariablesJson, ParametersJson). It is substituted without the secrets, so a secret or an
        // alias of one travels as its reference, which the child resolves from its own vault and classifies
        // as a secret, or refuses at launch when it cannot provide it. Never its clear value.
        var forwardable = WithoutSecrets(legVars, secretKeys);
        var (resolved, targetError) = await ResolveTriggerTargetAsync(runId, stepDef, forwardable, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            await FailTriggerStepAsync(runId, stepRun, targetError!, now, ct).ConfigureAwait(false);
            return;
        }
        var (run, target, targetName) = resolved;

        // Cycle guard mirrors the on_success handler: refuse if the target is already in the lineage
        // (immediate A→A or deeper A→B→A) or the chain has grown past the safety depth.
        var chain = new List<int>(PipelineUpstreamChain.Read(run.AdditionalVariablesJson)) { run.PipelineId };
        var chainError = ValidateTriggerChain(chain, target.Id, targetName);
        if (chainError is not null)
        {
            await FailTriggerStepAsync(runId, stepRun, chainError, now, ct).ConfigureAwait(false);
            return;
        }

        var vars = BuildSubstitutedValues(stepDef.Variables, forwardable, secretKeys);
        var parameters = BuildSubstitutedValues(stepDef.Parameters, forwardable, secretKeys);
        PropagateLocalDeploymentContext(legVars, vars);
        ApplyUpstreamContext(vars, run.Pipeline!.Name, runId, chain, run.Pipeline.TemplateReferenceName);
        var sourceError = ApplyTriggerSourceContext(stepDef, run, forwardable, vars);
        if (sourceError is not null)
        {
            await FailTriggerStepAsync(runId, stepRun, sourceError, now, ct).ConfigureAwait(false);
            return;
        }

        var (preparation, prepareError) = await PrepareAuthorizedRunAsync(target, targetName, vars, parameters, ct)
            .ConfigureAwait(false);
        if (preparation is null)
        {
            await FailTriggerStepAsync(runId, stepRun, prepareError!, now, ct).ConfigureAwait(false);
            return;
        }

        var reusedCheckpoint = await checkpoints.TryReuseCheckpointAsync(
            run, target, targetName, preparation, parameters, stepRun, now, ct).ConfigureAwait(false);
        if (reusedCheckpoint)
            return;

        // A "run to end" run tests everything, its triggered children included. Only that flag differs
        // from the preparation authorized above.
        var (child, startError) = await StartChildRunAsync(
            launcher, run.RunToEnd ? preparation with { RunToEnd = true } : preparation, vars, parameters, targetName,
            BuildTriggerStepIdempotencyKey(runId, stepRun.Id), ct).ConfigureAwait(false);
        if (startError is not null)
        {
            await FailTriggerStepAsync(runId, stepRun, startError, now, ct).ConfigureAwait(false);
            return;
        }

        // Leave the step Running (no agent task) - it now waits on the child run.
        stepRun.Status = TaskExecutionStatus.Running;
        stepRun.StartedAt ??= now;
        stepRun.TriggeredRunId = child!.Id;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task FailTriggerStepAsync(int runId, PipelineStepRun stepRun, string reason, DateTime now, CancellationToken ct)
    {
        MarkSystemStepFailed(stepRun, TaskFailureCodes.ToolError, reason, now);
        await repo.AppendRunWarningsAsync(
            runId, [PipelineStepFailureWarning.Trigger(stepRun.StepName, reason)], ct).ConfigureAwait(false);
    }

    private sealed record TriggerTarget(PipelineRun Run, Pipeline Target, string Name);

    /// <summary>The pipeline the step names, in the parent's project, or materialized from the run's
    /// immutable revision; otherwise why the step fails.</summary>
    private async Task<(TriggerTarget? Target, string? Error)> ResolveTriggerTargetAsync(
        int runId, PipelineStepDefinition stepDef, Dictionary<string, string> forwardable, CancellationToken ct)
    {
        var targetName = SubstituteVariables(stepDef.Pipeline ?? string.Empty, forwardable).Trim();
        if (string.IsNullOrWhiteSpace(targetName))
            return (null, "missing 'pipeline' name to trigger.");

        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var projectId = run?.Pipeline is null ? null : await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        if (projectId is not { } pid)
            return (null, "trigger is only supported for project-owned pipelines.");

        var target = await repo.FindPipelineByNameAndProjectAsync(targetName, pid, ct).ConfigureAwait(false)
            ?? await MaterializeMissingTriggerTargetAsync(run!, pid, targetName, ct).ConfigureAwait(false);
        return target is null
            ? (null, $"pipeline '{targetName}' not found in the project or at the immutable run revision.")
            : (new TriggerTarget(run!, target, targetName), null);
    }

    /// <summary>The child's preparation once its owner is authorized on the targets it produces, or why
    /// the step fails.</summary>
    private async Task<(PipelineRunPreparation? Preparation, string? Error)> PrepareAuthorizedRunAsync(
        Pipeline target, string targetName, Dictionary<string, string> vars,
        Dictionary<string, string> parameters, CancellationToken ct)
    {
        var (preparation, prepareError) = await PrepareTriggeredRunAsync(target.Id, targetName, vars, parameters, ct)
            .ConfigureAwait(false);
        if (prepareError is not null)
            return (null, prepareError);
        if (preparation is not null
            && await ChainedRunAuthorizedAsync(preparation, target.Name, target.CreatedByUsername, ct).ConfigureAwait(false))
            return (preparation, null);
        var ownerLabel = string.IsNullOrWhiteSpace(target.CreatedByUsername) ? "(none)" : target.CreatedByUsername;
        return (null, $"owner '{ownerLabel}' of pipeline '{targetName}' lacks Server.Admin on its target servers.");
    }

    /// <summary>
    /// Prepares the child run with the parameters the parent passes, or returns why the step fails.
    /// BACK-021: those parameters can choose the child's targets (agent: ${{ parameters.runner }}).
    /// Authorization reads the definition they produce, and the child starts from that same
    /// preparation, never from the one prepared without them. A null preparation with no error means
    /// the target could not be prepared, which the caller refuses like an unauthorized one.
    /// </summary>
    private async Task<(PipelineRunPreparation? Preparation, string? Error)> PrepareTriggeredRunAsync(
        int targetId, string targetName, Dictionary<string, string> vars,
        Dictionary<string, string> parameters, CancellationToken ct)
    {
        PipelineRunPreparation? preparation;
        try
        {
            preparation = await preparations.PrepareRunAsync(targetId, PipelineRunService.ResolveRunBranch(vars),
                yamlOverride: null, PipelineRunService.ResolveSourceCommit(vars), ct).ConfigureAwait(false);
        }
        catch (BadRequestException ex)
        {
            return (null, $"pipeline '{targetName}' configuration refused: {ex.Message}");
        }
        if (preparation is null)
            return (null, null);

        try
        {
            return (await parameterResolver.ResolveRunParametersAsync(preparation, parameters, ct).ConfigureAwait(false), null);
        }
        catch (BadRequestException ex)
        {
            return (null, $"pipeline '{targetName}' refused the trigger: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the child run, returning either it or the reason the step must fail.
    /// </summary>
    /// <remarks>
    /// The child row is persisted before its setup completes, so a backend interruption between that
    /// persistence and the parent link must replay the same child rather than orphan a new one on
    /// every recovery sweep - hence the idempotency key.
    ///
    /// The launch validates the parent's <c>parameters:</c> against the child's declared contract and
    /// refuses a missing required one, exactly as <c>PrepareRunAsync</c> refuses a bad configuration
    /// beforehand. Both must fail the step. Uncaught, that exception unwound the entire scheduling
    /// pass instead: the run kept the status Running with every step Pending, nothing marked and no
    /// warning recorded, and the reconcile sweep re-threw on it once a minute for as long as the
    /// backend stayed up. A run that reports nothing forever is worse than a failed one.
    /// </remarks>
    private static async Task<(PipelineRunDto? Child, string? Error)> StartChildRunAsync(
        IPipelineChildRunLauncher launcher,
        PipelineRunPreparation preparation,
        Dictionary<string, string> vars,
        Dictionary<string, string> parameters,
        string targetName,
        string idempotencyKey,
        CancellationToken ct)
    {
        try
        {
            var child = await launcher.TriggerPreparedRunAsync(
                preparation, vars, parameters, ct, idempotencyKey).ConfigureAwait(false);
            return child is null
                ? (null, $"could not start pipeline '{targetName}'.")
                : (child, null);
        }
        catch (BadRequestException ex)
        {
            return (null, $"pipeline '{targetName}' refused the trigger: {ex.Message}");
        }
    }

    private static string? ValidateTriggerChain(IReadOnlyCollection<int> chain, int targetId, string targetName)
    {
        if (chain.Contains(targetId))
            return $"cycle refused: '{targetName}' is already in the orchestration chain.";
        return chain.Count >= MaxTriggerChainDepth
            ? $"orchestration chain depth {chain.Count} reached the limit {MaxTriggerChainDepth}."
            : null;
    }

    private static Dictionary<string, string> WithoutSecrets(
        Dictionary<string, string> variables, IReadOnlySet<string> secretKeys) =>
        variables.Where(entry => !secretKeys.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> BuildSubstitutedValues(
        IReadOnlyDictionary<string, string> configured,
        Dictionary<string, string> forwardable,
        IReadOnlySet<string> secretKeys)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in configured)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            // A secret passed under its own name would reach the child as its own reference and replace
            // the child's vault value with that literal: the child reads its vault instead.
            if (secretKeys.Contains(key)
                && PipelineCommandBuilder.ReferencedVariableNames(value).Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;
            // A secret reference keeps its `:-default` too: the default must not stand in for the secret.
            values[key] = PipelineCommandBuilder.Substitute(value, forwardable, name => !secretKeys.Contains(name));
        }
        return values;
    }

    private static void PropagateLocalDeploymentContext(
        IReadOnlyDictionary<string, string> parentVariables,
        IDictionary<string, string> childVariables)
    {
        if (!parentVariables.TryGetValue(PipelineDeploymentTargetGuard.TargetVariable, out var target)
            || !string.Equals(target?.Trim(), PipelineDeploymentTargetGuard.Local, StringComparison.OrdinalIgnoreCase)
            || !parentVariables.TryGetValue(PipelineDeploymentTargetGuard.LocalAgentVariable, out var localAgent)
            || string.IsNullOrWhiteSpace(localAgent))
        {
            return;
        }

        // A local route is an orchestration boundary, not an optional child default.
        // Keep it sticky through every nested trigger so an audit cannot fall back to a real server.
        childVariables[PipelineDeploymentTargetGuard.TargetVariable] = PipelineDeploymentTargetGuard.Local;
        childVariables[PipelineDeploymentTargetGuard.LocalAgentVariable] = localAgent.Trim();
    }

    /// <summary>
    /// Git-strict projects may receive the parent definition before its newly-added child has been
    /// mirrored into the database (for example the first self-hosted release after adding a pipeline).
    /// Recover only from the exact immutable commit already pinned to the parent run. This is not a
    /// database-YAML fallback: an absent, malformed or differently-named Git definition still fails.
    /// </summary>
    private async Task<Pipeline?> MaterializeMissingTriggerTargetAsync(
        PipelineRun parentRun, int projectId, string targetName, CancellationToken ct)
    {
        // The definition's revision: with a source: block the run's own commit is the workspace's, in
        // another repository, where the target's definition does not exist.
        var definitionCommit = PipelineRunService.ResolveDefinitionCommit(parentRun);
        if (!IsGitCommitHash(definitionCommit) || parentRun.Pipeline is null)
            return null;

        var yaml = await pipelineGit.ReadProjectPipelineYamlAtRevisionAsync(
            projectId, targetName, definitionCommit!, ct,
            parentRun.Pipeline.SourceRepositoryId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(yaml))
            return null;

        var definition = YamlParsingHelper.ParseAndValidate(yaml, logger);
        if (definition is null
            || definition.Stages.Count == 0
            || !string.Equals(definition.Name?.Trim(), targetName, StringComparison.Ordinal))
            return null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var target = new Pipeline
        {
            Name = targetName,
            Description = "Materialized from an immutable Git trigger dependency.",
            YamlDefinition = yaml,
            TriggerType = definition.Trigger.Equals("webhook", StringComparison.OrdinalIgnoreCase)
                ? PipelineTriggerType.Webhook
                : definition.Trigger.Equals("schedule", StringComparison.OrdinalIgnoreCase)
                    ? PipelineTriggerType.Schedule
                    : PipelineTriggerType.Manual,
            ProjectId = projectId,
            SourceBranch = parentRun.BranchName,
            CreatedByUsername = parentRun.Pipeline.CreatedByUsername,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.AddPipelineAsync(target, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Materialized missing trigger pipeline '{Pipeline}' for project {ProjectId} from immutable commit {Commit}.",
            targetName, projectId, definitionCommit);
        return target;
    }

    /// <summary>Parent context is written after step variables so definitions cannot forge their
    /// parent or initiating origin.</summary>
    internal static void ApplyUpstreamContext(
        Dictionary<string, string> vars, string parentPipelineName, int parentRunId, IReadOnlyCollection<int> chain,
        string? parentTemplateName = null)
    {
        vars["UPSTREAM_RUN_ID"] = parentRunId.ToString();
        vars["UPSTREAM_PIPELINE"] = parentPipelineName;
        vars[PipelineScannerTaskFactory.UpstreamTemplateVariable] = parentTemplateName ?? string.Empty;
        vars["UPSTREAM_CHAIN"] = string.Join(",", chain);
        vars["BUILD_TRIGGEREDBY"] = $"trigger:{parentPipelineName}#{parentRunId}";
    }

    internal static string? ApplyTriggerSourceContext(
        PipelineStepDefinition step,
        PipelineRun parentRun,
        Dictionary<string, string> parentVariables,
        Dictionary<string, string> childVariables)
    {
        if (step.InheritSource)
        {
            // A parent whose workspace comes from another repository (source: block) hands down the
            // revision of its definition: that is the commit a pipeline of the same project exists at.
            var commit = PipelineRunService.ResolveDefinitionCommit(parentRun);
            var branch = PipelineRunService.ResolveDefinitionBranch(parentRun);
            if (IsGitCommitHash(commit))
                childVariables[PipelineRunService.SourceCommitVariable] = commit!;
            if (!string.IsNullOrWhiteSpace(branch))
                childVariables[PipelineRunService.SourceBranchVariable] = branch;
            return null;
        }

        childVariables.Remove(PipelineRunService.SourceCommitVariable);
        childVariables[PipelineRunService.SourceBranchVariable] = SubstituteVariables(step.SourceBranch ?? string.Empty, parentVariables).Trim();
        if (string.IsNullOrWhiteSpace(step.SourceCommit))
            return null;

        var pinnedCommit = SubstituteVariables(step.SourceCommit, parentVariables).Trim();
        if (!IsGitCommitHash(pinnedCommit))
            return "source_commit must resolve to a full hexadecimal Git commit hash.";

        childVariables[PipelineRunService.SourceCommitVariable] = pinnedCommit;
        return null;
    }

    private async Task<bool> ChainedRunAuthorizedAsync(
        PipelineRunPreparation preparation, string childPipelineName, string? owner, CancellationToken ct)
        => await ChainedRunRefusalAsync(preparation, childPipelineName, owner, ct).ConfigureAwait(false) is null;

    /// <summary>Why the owner may not launch the chained run (audited), or <c>null</c> when authorized.
    /// The two causes read differently so a reader knows whether to set an owner or to grant a
    /// permission.</summary>
    private async Task<PipelineProblem?> ChainedRunRefusalAsync(
        PipelineRunPreparation preparation, string childPipelineName, string? owner, CancellationToken ct)
    {
        var childPipelineId = preparation.PipelineId;
        if (string.IsNullOrEmpty(owner))
        {
            logger.LogWarning(
                "Chained run of pipeline {PipelineId} blocked (F-EXEC-1b): pipeline has no owner.", childPipelineId);
            await audit.LogAsync("BlockedUnownedChainedRun", "Pipeline", childPipelineId, childPipelineName, ct).ConfigureAwait(false);
            return PipelineProblem.Of(
                PipelineProblemCodes.ChainedPipelineNoOwner,
                $"pipeline '{childPipelineName}' has no owner, so an automated run cannot be authorized (re-save it as an authorized user)",
                ("pipeline", childPipelineName));
        }

        var targetIds = preparation.TargetServerIds;
        foreach (var serverId in targetIds)
        {
            if (!await authz.HasPermissionAsync(owner, ResourceType.Server, serverId, Permission.Admin, ct).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "Chained run of pipeline {PipelineId} blocked (F-EXEC-1b): owner '{Owner}' lacks " +
                    "Server.Admin on target server {ServerId}.", childPipelineId, owner, serverId);
                await audit.LogAsync("BlockedUnauthorizedChainedRun", "Pipeline", childPipelineId,
                    $"{childPipelineName} | Servers: {string.Join(',', targetIds)}", ct).ConfigureAwait(false);
                return PipelineProblem.Of(
                    PipelineProblemCodes.ChainedOwnerLacksServerAdmin,
                    $"owner '{owner}' of pipeline '{childPipelineName}' lacks Server.Admin on target server {serverId}",
                    ("owner", owner), ("pipeline", childPipelineName), ("server", serverId));
            }
        }
        return null;
    }

    public async Task<PipelineChainedRunOutcome> TriggerChainedRunAsync(
        int childPipelineId, Dictionary<string, string>? upstreamVariables,
        IPipelineChildRunLauncher launcher, CancellationToken ct)
    {
        var child = await repo.FindPipelineAsync(childPipelineId, ct).ConfigureAwait(false);
        if (child is null) return PipelineChainedRunOutcome.Refused(ChainedPipelineGone(childPipelineId));
        PipelineRunPreparation? preparation;
        try
        {
            preparation = await preparations.PrepareRunAsync(child.Id, PipelineRunService.ResolveRunBranch(upstreamVariables),
                yamlOverride: null, PipelineRunService.ResolveSourceCommit(upstreamVariables), ct).ConfigureAwait(false);
        }
        catch (BadRequestException refusal)
        {
            // Mandatory catch: a definition that cannot be prepared (invalid YAML, an illegal definition)
            // is the reason this hop is refused, and the caller must be able to say so on the upstream run.
            return PipelineChainedRunOutcome.Refused(
                PipelineProblem.Of(
                    PipelineProblemCodes.ChainedDefinitionNotPrepared,
                    $"its definition cannot be prepared: {refusal.Message}", ("detail", refusal.Message)),
                refusal.Problems);
        }
        if (preparation is null) return PipelineChainedRunOutcome.Refused(ChainedPipelineGone(childPipelineId));
        // F3: a chained target that declares a `candidateVersion` parameter (aetheus-deploy-prod, when
        // triggered by aetheus-candidate's on_success) receives it automatically from the upstream
        // release the trigger carried, so the queue-time manual entry stays optional rather than
        // required. Gated on the target actually declaring that parameter -
        // PipelineParameterResolver.TryResolve rejects any supplied key the target does not declare, so
        // seeding it unconditionally would break every OTHER chained target with an "Unknown parameter"
        // refusal.
        Dictionary<string, string>? parameters = null;
        if (upstreamVariables?.TryGetValue("UPSTREAM_RELEASE", out var upstreamRelease) == true
            && !string.IsNullOrWhiteSpace(upstreamRelease)
            && preparation.Definition.Parameters.Any(p => string.Equals(p.Name, "candidateVersion", StringComparison.OrdinalIgnoreCase)))
            parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["candidateVersion"] = upstreamRelease };
        // BACK-021: authorize the targets of the definition the parameters produce, then launch exactly it.
        var runtimePreparation = await parameterResolver.ResolveRunParametersAsync(preparation, parameters, ct)
            .ConfigureAwait(false);
        if (await ChainedRunRefusalAsync(runtimePreparation, child.Name, child.CreatedByUsername, ct).ConfigureAwait(false) is { } unauthorized)
            return PipelineChainedRunOutcome.Refused(unauthorized);
        var started = await launcher.TriggerPreparedRunAsync(runtimePreparation, upstreamVariables, parameters, ct).ConfigureAwait(false);
        return started is null
            ? PipelineChainedRunOutcome.Refused(
                PipelineProblem.Of(PipelineProblemCodes.ChainedLaunchReturnedNoRun, "the launch returned no run"))
            : PipelineChainedRunOutcome.Started(started);
    }

    private static PipelineProblem ChainedPipelineGone(int pipelineId) => PipelineProblem.Of(
        PipelineProblemCodes.ChainedPipelineGone, $"pipeline {pipelineId} no longer exists", ("pipeline", pipelineId));
}
