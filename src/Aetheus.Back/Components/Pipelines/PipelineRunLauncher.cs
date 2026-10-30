// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.GitGraph;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Starts runs. Everything from "this pipeline should run" to "the first stage has been dispatched and
/// the run row exists": resolving the trigger's variables and parameters, persisting the run
/// idempotently, and launching it - plus the automated (webhook/schedule) and rerun entry points.
/// </summary>
public interface IPipelineRunLauncher : IPipelineChildRunLauncher
{
    /// <inheritdoc cref="IPipelineRunService.TriggerRunAsync"/>
    Task<PipelineRunDto?> TriggerResolvedRunAsync(
        int id, Dictionary<string, string>? additionalVariables, Dictionary<string, string>? parameters,
        string? yamlOverride, string? commitOverride, CancellationToken ct, string? workspaceCommitOverride = null);

    /// <inheritdoc cref="IPipelineRunService.TriggerAutomatedRunAsync"/>
    Task<PipelineRunDto?> TriggerAutomatedRunAsync(
        int pipelineId, string triggerSource, Dictionary<string, string>? additionalVariables = null,
        CancellationToken ct = default);

    /// <inheritdoc cref="IPipelineRunService.PrepareAutomatedRunAsync"/>
    Task<PipelineRunPreparation?> PrepareAutomatedRunAsync(
        int pipelineId, string triggerSource, Dictionary<string, string>? additionalVariables = null,
        CancellationToken ct = default);

    /// <inheritdoc cref="IPipelineRunService.TriggerPreparedAutomatedRunAsync"/>
    Task<PipelineRunDto?> TriggerPreparedAutomatedRunAsync(
        PipelineRunPreparation preparation, string triggerSource,
        Dictionary<string, string>? additionalVariables = null, CancellationToken ct = default);

    /// <inheritdoc cref="IPipelineRunService.RecordRefusedAutomatedLaunchAsync"/>
    Task RecordRefusedAutomatedLaunchAsync(
        int pipelineId, string triggerSource, string reason,
        IReadOnlyDictionary<string, string>? additionalVariables = null, CancellationToken ct = default,
        IReadOnlyList<ProblemDto>? problems = null);

    /// <inheritdoc cref="IPipelineRunService.RerunAsync"/>
    Task<PipelineRunDto?> RerunAsync(int sourceRunId, RerunMode mode, CancellationToken ct = default);

    /// <inheritdoc cref="IPipelineRunService.PrepareRerunAsync"/>
    Task<PipelineRerunPlan?> PrepareRerunAsync(int sourceRunId, RerunMode mode, CancellationToken ct = default);
}

/// <summary>
/// The launch half of the engine, extracted from <see cref="PipelineRunService"/>. It is the real
/// implementation of <see cref="IPipelineChildRunLauncher"/> - the edge the trigger coordinator calls to
/// start a child run - which is what lets that recursion close through an interface instead of through
/// the engine calling itself.
///
/// Launch failure is deliberately visible: anything thrown after the run row exists is caught, written
/// to the run as a readable warning and the run is failed, never left Running with no task.
/// </summary>
public sealed class PipelineRunLauncher(
    IPipelineRepository repo,
    IHubContext<PipelineHub> pipelineHub,
    IPipelineVariableResolver variableResolver,
    IAuditService audit,
    IResourceAuthorizationService authz,
    IGitGraphRecorder gitGraph,
    IConfiguration configuration,
    IPipelineRunReader runReader,
    IPipelineStageDispatchPlanner planner,
    IPipelineRunParameterResolver parameterResolver,
    IPipelineRunPreparationService preparations,
    IPipelineRunPreflightService preflight,
    IPipelineConfigTemplatePreflight configTemplates,
    IPipelinePortRegistryGuard portGuard,
    PipelineRefusedLaunchRecorder refusals,
    TimeProvider timeProvider,
    ILogger<PipelineRunLauncher> logger) : IPipelineRunLauncher
{
    public async Task<PipelineRunDto?> TriggerResolvedRunAsync(int id,
        Dictionary<string, string>? additionalVariables, Dictionary<string, string>? parameters,
        string? yamlOverride, string? commitOverride, CancellationToken ct, string? workspaceCommitOverride = null)
    {
        var preparation = await preparations.PrepareRunAsync(
            id, PipelineRunService.ResolveRunBranch(additionalVariables), yamlOverride, commitOverride, ct,
            workspaceCommitOverride).ConfigureAwait(false);
        return preparation is null
            ? null
            : await TriggerPreparedRunAsync(preparation, additionalVariables, parameters, ct).ConfigureAwait(false);
    }

    public async Task<PipelineRunDto?> TriggerPreparedRunAsync(PipelineRunPreparation preparation,
        Dictionary<string, string>? additionalVariables = null,
        Dictionary<string, string>? parameters = null, CancellationToken ct = default,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        idempotencyKey = idempotencyKey?.Trim();
        if (idempotencyKey?.Length > PipelineRunRequest.MaxIdempotencyKeyLength)
            throw PipelineProblem.Of(
                PipelineProblemCodes.IdempotencyKeyTooLong,
                $"Idempotency key cannot exceed {PipelineRunRequest.MaxIdempotencyKeyLength} characters.",
                ("max", PipelineRunRequest.MaxIdempotencyKeyLength)).ToRefusal();
        var definition = preparation.Definition;
        PipelineReleaseNumbers.EnsureValid(definition);
        var trigger = ResolveTriggerVariables(preparation, additionalVariables, parameters);
        var variables = trigger.Variables;
        var effectiveParams = trigger.EffectiveParameters;
        var runtimePreparation = await ResolveRunParametersAsync(
            preparation, effectiveParams, ct).ConfigureAwait(false);

        var launchVars = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
        PipelineProjectVariables.Inject(
            launchVars, preparation.Pipeline.Project, configuration,
            preparation.BranchName ?? preparation.Pipeline.SourceBranch);
        if (!string.IsNullOrWhiteSpace(preparation.RepositoryUrl))
        {
            launchVars["BUILD_REPOSITORY_URI"] = preparation.RepositoryUrl;
            launchVars["REPOSITORY_URL"] = preparation.RepositoryUrl;
        }
        var (preLaunchVariables, _, _) = await variableResolver.ResolveVariablesWithWarningsAsync(
            runtimePreparation.Definition, runtimePreparation.EffectiveProjectId, launchVars, ct,
            pipelineId: preparation.PipelineId, pipelineName: preparation.Pipeline.Name,
            // Same reasoning as the preflight below: a reference nothing can ever satisfy is a refused
            // launch the caller sees immediately, not a deployment that renders the placeholder.
            enforceResolvedReferences: true)
            .ConfigureAwait(false);
        preflight.ValidateUniqueDeploymentTargets(runtimePreparation.Definition, preLaunchVariables);

        // Resolved TRANSITIVELY, like every other call site. Reading Pipeline.Project?.OrganizationId
        // yields null for a pipeline owned by an Environment or a ProjectServer, and a null
        // organization does not narrow the fleet, it opens it: the preflight then validates against
        // another organization's servers and the reservation below can be written on one of them.
        var organizationId = await repo
            .GetPipelineOrganizationIdAsync(preparation.PipelineId, ct).ConfigureAwait(false);

        // Ask for everything the run needs now, not an hour in. Deliberately placed before the run row
        // exists: a requirement that cannot be met is a refused launch the caller sees immediately,
        // not a run that dies at its fourth stage.
        var preflightOutcome = await preflight.FindBlockingProblemsAsync(
            runtimePreparation.Definition, preLaunchVariables,
            organizationId, runtimePreparation.EffectiveProjectId, ct)
            .ConfigureAwait(false);
        // R-32: a template a later stage renders is read now, so a token nothing defines refuses the
        // launch before any stage runs (aetheus-demo had already reset the demo when it was refused).
        // R-82: a template nobody can read is refused here too, before any stage touches the host.
        var templateFindings = await configTemplates.FindProblemsAsync(
            runtimePreparation.Definition, preLaunchVariables, runtimePreparation.EffectiveProjectId,
            preparation.DefinitionCommitHash ?? preparation.CommitHash, preparation.Pipeline.SourceRepositoryId, ct)
            .ConfigureAwait(false);
        // R-20: read on the definition as declared, before the parameters were substituted into it.
        var emptyTargetProblems = preflight.FindEmptyTargetParameterProblems(
            preparation.Definition, preLaunchVariables, effectiveParams);
        List<PipelineProblem> preflightProblems = [.. emptyTargetProblems, .. preflightOutcome.Problems, .. templateFindings.Problems];
        if (preflightProblems.Count > 0)
        {
            // Automated launch refusal is audited before throwing because no run row exists yet.
            // The informational entry links the refusal to the pipeline without duplicating
            // preflight warnings. The log, the audit and the message stay English; the problems
            // travel next to the message so the front can say them in the user's language.
            var reason = string.Join(" ", preflightProblems.Select(problem => problem.English));
            logger.LogInformation(
                "Preflight refused the launch of pipeline {PipelineId} ({PipelineName}): {Reason}",
                preparation.PipelineId, preparation.Pipeline.Name, reason);
            await audit.LogAsync(
                "BlockedByPreflight", "Pipeline", preparation.PipelineId,
                $"{preparation.Pipeline.Name} | {reason}", ct).ConfigureAwait(false);
            throw new BadRequestException(
                "This run cannot complete with the current configuration: " + reason,
                [.. preflightProblems.Select(problem => problem.ToDto())]);
        }
        // R-72: deploying despite the quality gate is checked before the run exists, like the preflight.
        var gateOverride = await PipelineGateOverride.CheckAsync(
            runtimePreparation, runtimePreparation.Definition, preLaunchVariables, repo, ct).ConfigureAwait(false);

        // The launch is accepted, so the ports it will bind are now this owner's. Declared here rather
        // than at deploy time on purpose: a port claimed only once the container is up is a port two
        // concurrent runs can both still believe is free.
        await portGuard.DeclareReservationsAsync(
            runtimePreparation.Definition, preLaunchVariables,
            organizationId, runtimePreparation.EffectiveProjectId,
            preparation.Pipeline.Project?.Name ?? preparation.Pipeline.Name, ct).ConfigureAwait(false);

        var run = CreatePipelineRun(
            preparation, runtimePreparation, variables, effectiveParams, idempotencyKey,
            preflightOutcome.Checks);
        var release = await PipelineReleaseNumbers.ResolveAsync(
            repo, runtimePreparation.Definition, runtimePreparation.EffectiveProjectId, variables,
            runtimePreparation.ReleaseSeries, ct).ConfigureAwait(false);
        run.ReleaseNumber = release?.Number;
        run.ReleaseSeries = release?.Series;
        if (gateOverride is not null)
            PipelineGateOverride.Apply(run, preparation.GateOverrideBy!, gateOverride, run.StartedAt);
        var existing = await PersistPipelineRunAsync(run, ct).ConfigureAwait(false);
        if (existing is not null) return existing;
        if (gateOverride is not null)
            await audit.LogAsync(
                "PipelineRunGateOverridden", "PipelineRun", run.Id,
                PipelineGateOverride.AuditDetails(preparation.Pipeline.Name, gateOverride), ct).ConfigureAwait(false);

        // Written once the run exists, which is the only place an operator can read them. A warning
        // kept in the server log alone would be invisible to the person whose deployment is about to
        // fight an undeclared listener for its port.
        List<string> preflightWarnings =
            [.. preflightOutcome.Warnings.Concat(templateFindings.Warnings).Select(warning => warning.ToStored())];
        if (preflightWarnings.Count > 0)
            await repo.AppendRunWarningsAsync(run.Id, preflightWarnings, ct).ConfigureAwait(false);

        return await LaunchPreparedRunAsync(
            run, preparation, runtimePreparation, variables, ct).ConfigureAwait(false);
    }

    private static ResolvedTriggerVariables ResolveTriggerVariables(
        PipelineRunPreparation preparation,
        Dictionary<string, string>? additionalVariables,
        Dictionary<string, string>? parameters)
    {
        var variables = additionalVariables is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(additionalVariables, StringComparer.OrdinalIgnoreCase);
        var reservedParameter = parameters?.Keys.FirstOrDefault(PipelineParameterResolver.IsReservedName);
        if (reservedParameter is not null)
            throw PipelineProblem.Of(
                PipelineProblemCodes.ParameterReservedName,
                $"Parameter '{reservedParameter}' uses a reserved system variable name.",
                ("parameter", reservedParameter)).ToRefusal();
        if (!PipelineParameterResolver.TryResolveProblems(
                preparation.Definition.Parameters, parameters, out var effective, out var errors))
            throw PipelineProblem.Refusal(errors);
        if (parameters is not null)
            foreach (var (key, value) in parameters) variables[key] = value;
        foreach (var (key, value) in effective)
            variables[PipelineCommandBuilder.ParameterKeyPrefix + key] = value;
        if (!string.IsNullOrWhiteSpace(preparation.BranchName))
            variables[PipelineRunService.SourceBranchVariable] = preparation.BranchName;
        if (IsGitCommitHash(preparation.CommitHash))
            variables[PipelineRunService.SourceCommitVariable] = preparation.CommitHash!;
        // Only the preparation says where the definition came from: a caller cannot hand these in.
        variables.Remove(PipelineRunService.DefinitionCommitVariable);
        variables.Remove(PipelineRunService.DefinitionBranchVariable);
        if (IsGitCommitHash(preparation.DefinitionCommitHash))
            variables[PipelineRunService.DefinitionCommitVariable] = preparation.DefinitionCommitHash!;
        if (!string.IsNullOrWhiteSpace(preparation.DefinitionBranchName))
            variables[PipelineRunService.DefinitionBranchVariable] = preparation.DefinitionBranchName;
        return new ResolvedTriggerVariables(variables, effective);
    }

    private PipelineRun CreatePipelineRun(
        PipelineRunPreparation preparation,
        PipelineRunPreparation runtimePreparation,
        Dictionary<string, string> variables,
        IReadOnlyDictionary<string, string> effectiveParameters,
        string? idempotencyKey,
        IReadOnlyList<PreflightCheckDto> preflightChecks) => new()
        {
            // Accepted runs retain the preflight checks that justified their launch.
            PreflightJson = preflightChecks.Count > 0 ? JsonSerializer.Serialize(preflightChecks) : null,
            PipelineId = preparation.PipelineId,
            RunToEnd = preparation.RunToEnd,
            Status = PipelineStatus.Running,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,
            YamlSnapshot = runtimePreparation.YamlSnapshot,
            BranchName = preparation.BranchName,
            CommitHash = preparation.CommitHash,
            RepositoryUrl = preparation.RepositoryUrl,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
            AdditionalVariablesJson = variables.Count > 0 ? JsonSerializer.Serialize(variables) : "{}",
            ParametersJson = effectiveParameters.Count > 0 ? JsonSerializer.Serialize(effectiveParameters) : "{}"
        };

    private async Task<PipelineRunDto?> PersistPipelineRunAsync(PipelineRun run, CancellationToken ct)
    {
        if (run.IdempotencyKey is null)
        {
            run.BuildNumber = await repo.ReserveNextBuildNumberAsync(run.PipelineId, ct).ConfigureAwait(false);
            await repo.AddPipelineRunAsync(run, ct).ConfigureAwait(false);
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            return null;
        }
        // Reserve before the idempotent insert so a genuinely new run carries a number. A replayed
        // launch returns the original row, and the number burnt here is simply skipped - build
        // numbers must never repeat, they are not required to be gapless.
        run.BuildNumber = await repo.ReserveNextBuildNumberAsync(run.PipelineId, ct).ConfigureAwait(false);
        var persisted = await repo.GetOrAddPipelineRunAsync(run, ct).ConfigureAwait(false);
        return persisted.Created ? null : await runReader.GetRunAsync(persisted.Run.Id, ct).ConfigureAwait(false);
    }

    private async Task<PipelineRunDto?> LaunchPreparedRunAsync(
        PipelineRun run,
        PipelineRunPreparation preparation,
        PipelineRunPreparation runtimePreparation,
        Dictionary<string, string> variables,
        CancellationToken ct)
    {
        try
        {
            return await LaunchRunAsync(run, runtimePreparation, variables, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a launch failure - let it propagate
        }
        catch (Exception ex)
        {
            // Product requirement: a failed launch must remain VISIBLE in the run list with its reason,
            // rather than a bare 500 with no row. Convert any setup failure (git read, invalid committed
            // YAML, variable resolution, task dispatch) into a failed run carrying the message. This is one
            // of the sanctioned try/catch cases - ErrorHandlingMiddleware cannot attach the failure to a
            // run it never sees.
            logger.LogError(ex, "Pipeline run {RunId} failed during launch setup", run.Id);
            await repo.AppendRunWarningsAsync(run.Id, [BuildLaunchFailureReason(ex)], ct).ConfigureAwait(false);
            await repo.UpdatePipelineRunStatusAsync(run.Id, PipelineStatus.Failed, ct).ConfigureAwait(false);
            await pipelineHub.Clients
                .Groups(HubGroups.PipelineRunUpdates(run.Id, preparation.PipelineId))
                .SendAsync("PipelineRunCompleted", run.Id, PipelineStatus.Failed, ct).ConfigureAwait(false);
            return await runReader.GetRunAsync(run.Id, ct).ConfigureAwait(false);
        }
    }

    private sealed record ResolvedTriggerVariables(
        Dictionary<string, string> Variables,
        Dictionary<string, string> EffectiveParameters);

    public Task<PipelineRunPreparation> ResolveRunParametersAsync(
        PipelineRunPreparation preparation,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct = default)
        => parameterResolver.ResolveRunParametersAsync(preparation, parameters, ct);

    // The user-facing reason persisted on a run that failed during launch setup (git read, invalid
    // committed YAML, variable resolution, dispatch). Secret values are masked upstream, so an exception
    // message here carries names/paths, not secrets.
    private static string BuildLaunchFailureReason(Exception ex)
        => PipelineProblem.Of(
            PipelineProblemCodes.LaunchSetupFailed, $"The run could not be launched: {ex.Message}", ("detail", ex.Message))
            .ToStored((ex as BadRequestException)?.Problems);

    // Post-persist launch setup. Git is deliberately not read here: authorization and execution must use
    // the same immutable preparation resolved before the run was created.
    private async Task<PipelineRunDto?> LaunchRunAsync(
        PipelineRun run, PipelineRunPreparation preparation,
        Dictionary<string, string> additionalVariables, CancellationToken ct)
    {
        var pipeline = preparation.Pipeline;
        var definition = preparation.Definition;
        var effectiveProjectId = preparation.EffectiveProjectId;

        // Inject project-level system vars that require a DB lookup.
        var projectVars = new Dictionary<string, string>(additionalVariables, StringComparer.OrdinalIgnoreCase);
        // S-TECH-RPVI: single injection point shared with PipelineVariableResolver so the trigger-time
        // snapshot and the per-run re-resolution can never diverge (which once froze the localhost mirror
        // authority on the first-stage clone). The mirror URL is rehomed inside Inject.
        PipelineProjectVariables.Inject(
            projectVars, pipeline.Project, configuration, run.BranchName ?? pipeline.SourceBranch);
        if (!string.IsNullOrWhiteSpace(preparation.RepositoryUrl))
        {
            projectVars["BUILD_REPOSITORY_URI"] = preparation.RepositoryUrl;
            projectVars["REPOSITORY_URL"] = preparation.RepositoryUrl;
        }
        if (!string.IsNullOrWhiteSpace(run.CommitHash))
            projectVars["BUILD_SOURCEVERSION"] = run.CommitHash;
        PipelineReleaseNumbers.Inject(projectVars, run.ReleaseNumber, run.ReleaseSeries);

        var (resolvedVars, warnings, secretKeys) = await variableResolver.ResolveVariablesWithWarningsAsync(
            definition, effectiveProjectId, projectVars, ct,
            pipelineId: run.PipelineId, runId: run.Id, pipelineName: pipeline.Name,
            buildNumber: run.BuildNumber).ConfigureAwait(false);
        var publicVars = FilterSecretKeys(resolvedVars, secretKeys);
        run.ResolvedVariablesJson = JsonSerializer.Serialize(publicVars);
        warnings.InsertRange(0, preparation.DefinitionWarnings);
        run.WarningsJson = warnings.Count > 0 ? JsonSerializer.Serialize(warnings) : null;

        foreach (var stepRun in PipelineStepRunPlan.Build(run, preparation))
            repo.TrackPipelineStepRun(stepRun);

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        await pipelineHub.Clients.Groups(HubGroups.PipelineRunUpdates(run.Id, run.PipelineId)).SendAsync("PipelineRunStarted", run.Id, run.PipelineId, ct).ConfigureAwait(false);

        await planner.CreateTasksForNextStageAsync(run.Id, definition, resolvedVars, secretKeys, this, ct).ConfigureAwait(false);

        await audit.LogAsync("Triggered", "PipelineRun", run.Id, pipeline.Name, ct).ConfigureAwait(false);

        // Record git provenance so the run-detail view can resolve internal commit/branch pages and
        // the cross-link graph stays current beyond the one-shot migration backfill (best-effort).
        if (effectiveProjectId is { } recProjectId)
            await gitGraph.RecordRunContextAsync(recProjectId, run.CommitHash, run.BranchName, ct).ConfigureAwait(false);

        return await runReader.GetRunAsync(run.Id, ct).ConfigureAwait(false);
    }

    public async Task<PipelineRunDto?> TriggerAutomatedRunAsync(
        int pipelineId, string triggerSource, Dictionary<string, string>? additionalVariables = null, CancellationToken ct = default)
    {
        var preparation = await PrepareAutomatedRunAsync(
            pipelineId, triggerSource, additionalVariables, ct).ConfigureAwait(false);
        return preparation is null
            ? null
            : await TriggerPreparedAutomatedRunAsync(preparation, triggerSource, additionalVariables, ct).ConfigureAwait(false);
    }

    public async Task<PipelineRunDto?> TriggerPreparedAutomatedRunAsync(
        PipelineRunPreparation preparation, string triggerSource,
        Dictionary<string, string>? additionalVariables = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        try
        {
            return await TriggerPreparedRunAsync(preparation, additionalVariables, ct: ct).ConfigureAwait(false);
        }
        catch (BadRequestException refusal)
        {
            // Mandatory catch : nobody is in front of an automated trigger to read the refusal, and
            // no run row exists to carry it. It is rethrown once a failed run carries it and the
            // subscribers are told.
            await refusals.RecordAsync(
                preparation.Pipeline, preparation, triggerSource, refusal.Message, additionalVariables, ct, refusal.Problems)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task RecordRefusedAutomatedLaunchAsync(
        int pipelineId, string triggerSource, string reason,
        IReadOnlyDictionary<string, string>? additionalVariables = null, CancellationToken ct = default,
        IReadOnlyList<ProblemDto>? problems = null)
    {
        var pipeline = await repo.FindPipelineAsync(pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null) return;
        await refusals.RecordAsync(pipeline, null, triggerSource, reason, additionalVariables, ct, problems).ConfigureAwait(false);
    }

    public async Task<PipelineRunPreparation?> PrepareAutomatedRunAsync(
        int pipelineId,
        string triggerSource,
        Dictionary<string, string>? additionalVariables = null,
        CancellationToken ct = default)
    {
        var pipeline = await repo.FindPipelineAsync(pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null) return null;
        // Recette R-51 (ADR-058): a deleted project waiting for its server cleanup runs nothing, and a webhook or
        // a schedule is no one to tell: the trigger is dropped, logged, without a refused run.
        if ((pipeline.ProjectId ?? await repo.GetPipelineProjectIdAsync(pipeline, ct).ConfigureAwait(false)) is { } projectId
            && await repo.IsProjectDeletionPendingAsync(projectId, ct).ConfigureAwait(false))
        {
            logger.LogInformation("{Trigger} run of pipeline {PipelineId} dropped: its project {ProjectId} is deleted and waits for its server cleanup.",
                triggerSource, pipelineId, projectId);
            return null;
        }

        // F-EXEC-1b: a pipeline step is free-form shell (= RCE). Interactive runs authorize the
        // live caller (F-EXEC-1); webhook/scheduler have no principal, so authorize the owner.
        var owner = pipeline.CreatedByUsername;
        if (string.IsNullOrEmpty(owner))
        {
            logger.LogWarning(
                "{Trigger} run of pipeline {PipelineId} blocked (F-EXEC-1b): pipeline has no owner. " +
                "Re-save it as an authorized user to enable automated triggers.", triggerSource, pipelineId);
            await audit.LogAsync("BlockedUnownedAutomatedRun", "Pipeline", pipelineId, pipeline.Name, ct).ConfigureAwait(false);
            return null;
        }

        PipelineRunPreparation? preparation;
        try
        {
            preparation = await preparations.PrepareRunAsync(pipelineId, PipelineRunService.ResolveRunBranch(additionalVariables),
                yamlOverride: null, PipelineRunService.ResolveSourceCommit(additionalVariables), ct).ConfigureAwait(false);
        }
        catch (BadRequestException refusal)
        {
            // Mandatory catch, as in TriggerPreparedAutomatedRunAsync: a definition that cannot be
            // prepared (invalid YAML, a source repository that differs or cannot be read) is a
            // refusal nobody reads unless a failed run carries it.
            await refusals.RecordAsync(pipeline, null, triggerSource, refusal.Message, additionalVariables, ct, refusal.Problems)
                .ConfigureAwait(false);
            throw;
        }
        if (preparation is null) return null;
        var targetIds = preparation.TargetServerIds;
        foreach (var serverId in targetIds)
        {
            if (!await authz.HasPermissionAsync(owner, ResourceType.Server, serverId, Permission.Admin, ct).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "{Trigger} run of pipeline {PipelineId} blocked (F-EXEC-1b): owner '{Owner}' lacks " +
                    "Server.Admin on target server {ServerId}.", triggerSource, pipelineId, owner, serverId);
                await audit.LogAsync("BlockedUnauthorizedAutomatedRun", "Pipeline", pipelineId,
                    $"{pipeline.Name} | Servers: {string.Join(',', targetIds)}", ct).ConfigureAwait(false);
                return null;
            }
        }

        // F-026: log the resolved server list so automated-run audits are traceable.
        await audit.LogAsync("AutomatedRunAuthorized", "Pipeline", pipelineId,
            $"{pipeline.Name} | Servers: {string.Join(',', targetIds)}", ct).ConfigureAwait(false);

        return preparation;
    }

    public async Task<PipelineRunDto?> RerunAsync(int sourceRunId, RerunMode mode, CancellationToken ct = default)
    {
        var plan = await PrepareRerunAsync(sourceRunId, mode, ct).ConfigureAwait(false);
        return plan is null
            ? null
            : await TriggerPreparedRunAsync(plan.Preparation, plan.AdditionalVariables, plan.Parameters, ct)
                .ConfigureAwait(false);
    }

    public async Task<PipelineRerunPlan?> PrepareRerunAsync(int sourceRunId, RerunMode mode, CancellationToken ct = default)
    {
        var source = await repo.GetRunDetailAsync(sourceRunId, ct).ConfigureAwait(false);
        if (source is null) return null;

        // Reproduce the original queue-time inputs (user-supplied additional variables).
        var vars = DeserializeResolvedVariables(source.AdditionalVariablesJson);
        Dictionary<string, string>? additional = vars.Count > 0 ? vars : null;

        // P: replay the original run's queue-time parameters so the rerun's snapshot/precedence match
        // (the user values are also carried in `additional`; re-passing them is idempotent).
        var sourceParams = DeserializeResolvedVariables(source.ParametersJson);
        var rerunParams = sourceParams.Count > 0 ? sourceParams : null;

        // Snapshot modes need the captured YAML; a legacy run without one falls back to a fresh run
        // from the live definition (honest degradation - never a silent no-op).
        if (mode == RerunMode.ResumeCheckpoints)
        {
            if (string.IsNullOrWhiteSpace(source.YamlSnapshot) || !IsGitCommitHash(source.CommitHash))
                return null;
            additional ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            additional[PipelineRunService.ResumeSourceRunVariable] = sourceRunId.ToString();
        }

        // A run with a source: block records two revisions, the definition's and the workspace's
        // (its own CommitHash). A same-commit rerun reads the definition at the first and checks
        // out the second; without the block both are the run's commit.
        var definitionCommit = PipelineRunService.ResolveDefinitionCommit(source);
        var workspaceCommit = string.Equals(definitionCommit, source.CommitHash, StringComparison.OrdinalIgnoreCase)
            ? null
            : source.CommitHash;

        var (yamlOverride, commitOverride, workspaceOverride) = mode switch
        {
            RerunMode.SnapshotSameCommit when !string.IsNullOrWhiteSpace(source.YamlSnapshot) =>
                (source.YamlSnapshot, definitionCommit, workspaceCommit),
            RerunMode.SnapshotBranchHead when !string.IsNullOrWhiteSpace(source.YamlSnapshot) =>
                (source.YamlSnapshot, null, null),
            RerunMode.ResumeCheckpoints => (source.YamlSnapshot, definitionCommit, workspaceCommit),
            _ => ((string?)null, (string?)null, (string?)null)
        };
        var preparation = await preparations.PrepareRunAsync(
            source.PipelineId, PipelineRunService.ResolveRunBranch(additional), yamlOverride, commitOverride, ct,
            workspaceOverride).ConfigureAwait(false);
        if (preparation is null) return null;
        // BACK-020: the replayed parameters can choose targets, so the plan is authorized with them applied.
        var runtimePreparation = await parameterResolver.ResolveRunParametersAsync(preparation, rerunParams, ct)
            .ConfigureAwait(false);
        return new PipelineRerunPlan(runtimePreparation, additional, rerunParams);
    }
}
