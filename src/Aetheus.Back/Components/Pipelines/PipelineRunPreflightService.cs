// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Answers "can this run reach its end at all?" before a single task is dispatched.
///
/// The stage-level gates (runner resolution, environment checks P-22) are evaluated when each stage
/// is dispatched, which is correct - the fleet changes while a run is in flight. But it means a
/// qualification chain can spend an hour in CI, Quality and Security before discovering that the QA
/// stage names an environment whose required check refuses, or a pool nobody is configured for.
/// This runs the same checks up front and refuses the launch instead.
///
/// It never duplicates the decision, only anticipates it: the dispatch-time gates stay exactly as
/// they are, and this refuses only on conditions that cannot resolve themselves, never on a runner
/// that is merely offline right now (that case is the scheduler's stand-by path, not an error).
/// </summary>
public interface IPipelineRunPreflightService
{
    /// <summary>No problem when the run may start; otherwise one readable reason per problem found.
    /// The outcome also carries what was verified, which the launcher snapshots onto the run: without
    /// it an accepted launch leaves no trace of what the preflight looked at. Observations that do not
    /// refuse the launch travel in <c>Warnings</c> (PLAN-005 observed_port_policy), never in
    /// <c>Problems</c>: a note read as a refusal would block a launch nobody meant to block.</summary>
    Task<PipelinePreflightOutcomeDto> FindBlockingProblemsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        int? projectId,
        CancellationToken ct);

    /// <summary>
    /// Refuses two deploy steps that would land the same app on the same target. Two stages resolving
    /// to one runner make the second deployment overwrite the first, which reads as a mysterious
    /// rollback rather than a configuration error.
    /// </summary>
    void ValidateUniqueDeploymentTargets(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> variables);
}

public sealed class PipelineRunPreflightService(
    IPipelineRepository repo,
    IPipelineDispatchServerResolver dispatchServers,
    IPipelineEnvironmentCheckGuard environmentChecks,
    IPipelinePortRegistryGuard portGuard,
    IPipelineChildPipelineResolver children,
    IPipelineRequirementsChecker requirements,
    IPipelineScannerManifestPreflight scannerManifest,
    IPipelineReleaseArtifactPreflight releaseArtifacts,
    ILogger<PipelineRunPreflightService> logger) : IPipelineRunPreflightService
{
    public async Task<PipelinePreflightOutcomeDto> FindBlockingProblemsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        int? projectId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(resolvedVariables);

        var problems = new List<string>();
        // Shared across the whole chain: one fleet query per distinct selector and one environment
        // probe per distinct environment, however many pipelines name them.
        var state = new ChainPreflightState();
        await CheckOnePipelineAsync(
            definition, resolvedVariables, resolvedVariables.GetValueOrDefault("UPSTREAM_PIPELINE"),
            resolvedVariables.GetValueOrDefault(PipelineScannerTaskFactory.UpstreamTemplateVariable),
            sameSourceCommit: true, organizationId, projectId, problems, state, ct).ConfigureAwait(false);

        if (projectId is { } childProjectId)
            await CheckChildPipelinesAsync(
                definition, definition.Name,
                await RootTemplateAsync(definition, childProjectId, ct).ConfigureAwait(false),
                true, resolvedVariables, organizationId, childProjectId,
                problems, state, depth: 1, path: string.Empty, ct).ConfigureAwait(false);

        if (problems.Count > 0)
            PipelinePreflightRefusalLog.Write(logger, definition.Name, problems);

        return new PipelinePreflightOutcomeDto
        {
            Problems = problems,
            Checks = state.Checks,
            Warnings = state.Warnings,
        };
    }

    private async Task CheckOnePipelineAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        string? upstreamPipeline,
        string? upstreamTemplate,
        bool sameSourceCommit,
        int? organizationId,
        int? projectId,
        List<string> problems,
        ChainPreflightState state,
        CancellationToken ct)
    {
        // The declaration first: a template that says what it needs must be judged on that, and a
        // missing library is a launch refusal rather than the fourth stage failing on an empty value.
        await CheckDeclaredRequirementsAsync(definition, organizationId, projectId, problems, state, ct)
            .ConfigureAwait(false);

        var stages = YamlParsingHelper.FlattenJobs(definition).ToList();

        // Each of the three checks below is deduplicated on its own key, because a launch is on the
        // synchronous request path and every one of them was previously repeated per stage: the fleet
        // query per selector, an outbound DNS+HTTP call per environment check, and one database round
        // trip per referenced pipeline NAME. A ten-stage pipeline paid all three ten times over.
        foreach (var stage in stages)
            await CheckRunnerIsConfiguredAsync(
                stage, resolvedVariables, organizationId, problems, state, ct).ConfigureAwait(false);

        // After the runner check: a stage nobody is configured for is refused there, and this one only
        // asks whether a configured runner can serve the candidate's scanner steps.
        var manifestProblem = await scannerManifest.FindProblemAsync(
            definition, resolvedVariables, upstreamPipeline, upstreamTemplate, organizationId, ct).ConfigureAwait(false);
        if (manifestProblem is not null)
        {
            problems.Add(manifestProblem);
            state.Record(PreflightCheckKinds.ScannerManifest, definition.Name ?? string.Empty,
                satisfied: false, detail: manifestProblem);
        }

        // Only on the commit the launch knows: an on_success child starts later, on its own commit.
        if (sameSourceCommit && projectId is { } releaseProjectId)
        {
            var releaseProblems = await releaseArtifacts.FindProblemsAsync(
                definition, resolvedVariables, releaseProjectId, ct).ConfigureAwait(false);
            problems.AddRange(releaseProblems);
            foreach (var releaseProblem in releaseProblems)
                state.Record(PreflightCheckKinds.ReleaseArtifact, releaseProblem, satisfied: false, detail: releaseProblem);
        }

        foreach (var environmentGroup in stages
                     .Where(stage => !string.IsNullOrEmpty(stage.Environment))
                     .GroupBy(stage => stage.Environment!, StringComparer.OrdinalIgnoreCase))
        {
            if (!state.CheckedEnvironments.Add(environmentGroup.Key)) continue;
            await CheckEnvironmentAdmitsTheRunAsync(environmentGroup, projectId, problems, state, ct).ConfigureAwait(false);
        }

        if (projectId is { } id)
            await CheckReferencedPipelinesExistAsync(
                definition, stages, resolvedVariables, id, problems, state, ct).ConfigureAwait(false);

        // A port another project already holds on the target host cannot resolve itself either: the
        // run would spend its whole build to die at `docker up` on "port is already allocated", a
        // message that names no holder. Refuse here, naming one.
        var portConflicts = await portGuard.FindPortConflictsAsync(
            definition, resolvedVariables, organizationId, projectId, ct).ConfigureAwait(false);
        problems.AddRange(portConflicts.Blocking);
        state.Warnings.AddRange(portConflicts.Warnings);
        // Satisfied on the blocking set alone: an observation the policy chose not to refuse must not
        // turn the recorded check into a failure the run reports as unmet.
        state.Record(
            PreflightCheckKinds.Ports,
            definition.Name ?? string.Empty,
            portConflicts.Blocking.Count == 0,
            portConflicts.Blocking.Count == 0 ? null : string.Join(" ", portConflicts.Blocking));
    }

    /// <summary>
    /// The <c>requires:</c> block. Deduplicated across the chain by the same state as everything else,
    /// so an orchestrator and its four children declaring the same library produce one requirement.
    /// </summary>
    private async Task CheckDeclaredRequirementsAsync(
        PipelineYamlDefinition definition,
        int? organizationId,
        int? projectId,
        List<string> problems,
        ChainPreflightState state,
        CancellationToken ct)
    {
        foreach (var outcome in await requirements
            .CheckAsync(definition.Requires, projectId, organizationId, ct).ConfigureAwait(false))
        {
            if (outcome.Problem is { } problem) problems.Add(problem);
            state.Record(outcome.Kind, outcome.Name, outcome.Satisfied, outcome.Problem);
        }
    }

    /// <summary>The classes of requirement a recorded check can belong to. Strings, not an enum: they
    /// are written into the run's JSON snapshot and read back by runs older than any later change.</summary>
    internal static class PreflightCheckKinds
    {
        public const string Runner = "runner";
        public const string Environment = "environment";
        public const string PipelineReference = "pipeline-reference";
        public const string Ports = "ports";
        public const string ChildPipeline = "child-pipeline";
        public const string ScannerManifest = "scanner-manifest";
        public const string ReleaseArtifact = "release-artifact";
    }

    /// <summary>
    /// Follows `type: trigger` steps and `on_success` entries and applies the same checks to each
    /// pipeline they name.
    ///
    /// This is what the preflight existed for and did not do. An orchestrator like aetheus-candidate
    /// declares no environment and no variable library of its own: everything it needs belongs to the
    /// four pipelines it triggers and to the deployment it proposes. So its launch passed every check
    /// and the chain still died an hour in, on a `qa` environment whose required check refuses, or two
    /// hours in, at the deployment, on a variable library entry nobody had created.
    ///
    /// Deliberately conservative about variables: a child is resolved with the parent's own resolved
    /// variables, plus every name the triggering step forwards, plus the child's declared parameter
    /// names. Anything that could legitimately be supplied at run time is therefore considered
    /// supplied, because a false refusal here blocks every launch and would be far worse than the late
    /// failure it replaces. What remains refused is what nothing can ever provide: a library entry, a
    /// vault, an environment or a runner that does not exist.
    /// </summary>
    /// <summary>Recette R2-121: the shipped template the launched pipeline extends, which is what makes
    /// portfolio-candidate a candidate for its children's scanner manifest, as aetheus-candidate is by name.</summary>
    private async Task<string?> RootTemplateAsync(PipelineYamlDefinition definition, int projectId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(definition.Name)
            ? null
            : (await repo.FindPipelineByNameAndProjectAsync(definition.Name, projectId, ct).ConfigureAwait(false))
                ?.TemplateReferenceName;

    private async Task CheckChildPipelinesAsync(
        PipelineYamlDefinition definition,
        string? parentPipelineName,
        string? parentTemplateName,
        bool parentOnSameCommit,
        IReadOnlyDictionary<string, string> parentVariables,
        int? organizationId,
        int projectId,
        List<string> problems,
        ChainPreflightState state,
        int depth,
        string path,
        CancellationToken ct)
    {
        if (depth > PipelineChildPipelineResolver.MaxChildDepth) return;

        var stillToCome = PipelineUnresolvedVariableGuard.BuildDeferredNames(definition);
        foreach (var named in children.References(definition))
        {
            // A trigger step's name is substituted at dispatch (an on_success name is not), so the
            // chain followed is the one the run will start. A name only known later is left to the
            // warning the reference check records.
            var reference = named.FromOnSuccess
                ? named
                : named with { Name = ExpandAsDispatched(named.Name, parentVariables, stillToCome) };
            if (!reference.FromOnSuccess && !IsKnownAtLaunch(reference.Name)) continue;
            if (!state.VisitedPipelines.Add(reference.Name)) continue;
            var childPath = path.Length == 0 ? reference.Name : $"{path} -> {reference.Name}";
            var target = await repo.FindPipelineByNameAndProjectAsync(reference.Name, projectId, ct)
                .ConfigureAwait(false);
            if (target is null)
            {
                // A trigger step's missing target is already reported by name above; an on_success
                // entry has no step to report it, so it is named here.
                if (reference.FromOnSuccess)
                    problems.Add(
                        $"on_success names pipeline '{reference.Name}', which does not exist in this project.");
                continue;
            }

            var childDefinition = await children.ResolveDefinitionAsync(
                target, organizationId, childPath, problems, ct).ConfigureAwait(false);
            if (childDefinition is null) continue;

            var childVariables = await children.ResolveVariablesAsync(
                childDefinition, target, reference, parentVariables, projectId, childPath, problems, ct)
                .ConfigureAwait(false);
            if (childVariables is null) continue;

            // Collected apart, then republished with the chain that reaches this child, so a refusal
            // names the pipeline and stage at fault rather than an anonymous requirement.
            // The child runs with UPSTREAM_PIPELINE set to its parent's name (trigger step and
            // on_success alike), which is what decides whether its scanners need the candidate manifest.
            var childProblems = new List<string>();
            var childOnSameCommit = parentOnSameCommit && !reference.FromOnSuccess && reference.SameSourceCommit;
            await CheckOnePipelineAsync(
                childDefinition, childVariables, parentPipelineName, parentTemplateName, childOnSameCommit, organizationId,
                projectId, childProblems, state, ct).ConfigureAwait(false);
            problems.AddRange(childProblems.Select(problem => $"{childPath}: {problem}"));
            // The chain itself is a requirement: reaching this line means the child's definition
            // resolved and its variables could be supplied, which is what an orchestrator fails on.
            state.Record(
                PreflightCheckKinds.ChildPipeline, childPath,
                childProblems.Count == 0,
                childProblems.Count == 0 ? null : string.Join(" ", childProblems));

            await CheckChildPipelinesAsync(
                childDefinition, target.Name, target.TemplateReferenceName, childOnSameCommit, childVariables,
                organizationId, projectId,
                problems, state, depth + 1, childPath, ct).ConfigureAwait(false);
        }
    }

    private sealed class ChainPreflightState
    {
        public Dictionary<string, IReadOnlyCollection<int>> CandidateCache { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<PipelineRunnerFacts>> FactsCache { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CheckedEnvironments { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> VisitedPipelines { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What was verified, in the order it was verified, across the whole chain.</summary>
        public List<PreflightCheckDto> Checks { get; } = [];

        /// <summary>Observations that are recorded on the run without refusing it: an undeclared
        /// listener under <c>observed_port_policy: warning</c> (PLAN-005). Collected here for the same
        /// reason as the checks, so a child pipeline's observation is not lost with its own state.</summary>
        public List<string> Warnings { get; } = [];

        private readonly HashSet<string> _recorded = new(StringComparer.Ordinal);

        /// <summary>Records one verified requirement. Deduplicated on kind+subject, because the same
        /// selector or environment is legitimately named by many stages and by more than one pipeline
        /// of the chain: the record should read as a list of requirements, not of stages.</summary>
        public void Record(string kind, string subject, bool satisfied = true, string? detail = null)
        {
            if (!_recorded.Add(kind + "|" + subject)) return;
            Checks.Add(new PreflightCheckDto
            {
                Kind = kind,
                Subject = subject,
                Satisfied = satisfied,
                Detail = detail
            });
        }
    }

    public void ValidateUniqueDeploymentTargets(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(variables);
        // SubstituteVariables takes a concrete Dictionary; the callers hold one, but the interface is
        // read-only so the contract cannot be widened by accident.
        var substitutions = variables as Dictionary<string, string>
            ?? new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
        {
            var target = !string.IsNullOrWhiteSpace(stage.Environment)
                ? $"environment:{PipelineRunHelpers.SubstituteVariables(stage.Environment, substitutions)}"
                : !string.IsNullOrWhiteSpace(stage.Pool)
                    ? $"pool:{PipelineRunHelpers.SubstituteVariables(stage.Pool, substitutions)}"
                    : $"agent:{PipelineRunHelpers.SubstituteVariables(stage.Agent ?? string.Empty, substitutions)}";
            foreach (var step in stage.Steps.Where(item =>
                         string.Equals(item.Type, "deploy", StringComparison.OrdinalIgnoreCase)))
            {
                var app = PipelineRunHelpers.SubstituteVariables(step.App ?? string.Empty, substitutions).Trim();
                if (app.Length == 0) continue;
                var key = $"{target}|app:{app}";
                var path = $"stage '{stage.Name}' / step '{step.Name}'";
                if (seen.TryGetValue(key, out var first))
                    throw new BadRequestException(
                        $"Duplicate deployment target '{app}' on {target}: {first} and {path}. Override the inherited step by using the same name or remove one deployment.");
                seen[key] = path;
            }
        }
    }

    /// <summary>
    /// Refuses only when NO server is configured for the stage's selectors. A configured server that
    /// is currently offline is not a preflight failure: the planner already parks the run and picks
    /// it up when the runner comes back, and turning that into a launch error would make a transient
    /// outage look like a broken pipeline.
    /// </summary>
    private async Task CheckRunnerIsConfiguredAsync(
        PipelineStageDefinition stage,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        List<string> problems,
        ChainPreflightState state,
        CancellationToken ct)
    {
        var candidateCache = state.CandidateCache;
        var effective = dispatchServers.ResolveEffectiveStageTarget(stage, resolvedVariables);
        var deploymentStage = dispatchServers.StageHasDeployStep(stage);
        // Stages of one pipeline overwhelmingly share a selector, and the answer cannot change during a
        // single preflight, so the fleet is queried once per DISTINCT selector rather than once per stage.
        var cacheKey = string.Join(
            "|",
            effective.Pool, effective.Environment, effective.Agent, effective.Os,
            organizationId?.ToString(CultureInfo.InvariantCulture), deploymentStage);
        if (!candidateCache.TryGetValue(cacheKey, out var configured))
        {
            configured = await repo.FindCandidateTargetServerIdsAsync(
                effective.Pool, effective.Environment, effective.Agent,
                OsTypeHelper.Parse(effective.Os), organizationId, deploymentStage, ct).ConfigureAwait(false);
            candidateCache[cacheKey] = configured;
        }

        var selectorLabel = DescribeSelector(effective, deploymentStage);
        if (configured.Count > 0)
        {
            var incapable = await FindIncapableRunnerReasonAsync(
                stage, configured, deploymentStage, state, cacheKey, ct).ConfigureAwait(false);
            if (incapable is not null) problems.Add(incapable);
            state.Record(PreflightCheckKinds.Runner, selectorLabel, incapable is null, incapable);
            return;
        }

        // A stage with no selector at all falls back to any runner in the organization; the candidate
        // query above cannot express that, so it is left to dispatch rather than refused here.
        var hasSelector = !string.IsNullOrEmpty(effective.Pool)
                          || !string.IsNullOrEmpty(effective.Environment)
                          || !string.IsNullOrEmpty(effective.Agent)
                             && !effective.Agent.Equals("default", StringComparison.OrdinalIgnoreCase);
        if (!hasSelector && !deploymentStage) return;

        var reason = deploymentStage
            ? dispatchServers.BuildNoDeployTargetReason(stage.Name, stage)
            : dispatchServers.BuildNoServerReason(stage.Name, stage);
        problems.Add(reason);
        state.Record(PreflightCheckKinds.Runner, selectorLabel, satisfied: false, detail: reason);
    }

    /// <summary>
    /// Configured is not enough: dispatch only hands a stage to an agent inside the supported protocol
    /// window that advertises the capability the stage needs (<c>WhereAgentCan</c>). When no configured
    /// runner qualifies, the planner parks the stage as "waiting for a runner" for good, since what
    /// would release it is an agent update, an explicit fleet operation. Offline runners are judged on
    /// their last report, like everywhere else in the preflight.
    /// </summary>
    private async Task<string?> FindIncapableRunnerReasonAsync(
        PipelineStageDefinition stage,
        IReadOnlyCollection<int> configured,
        bool deploymentStage,
        ChainPreflightState state,
        string cacheKey,
        CancellationToken ct)
    {
        if (!state.FactsCache.TryGetValue(cacheKey, out var facts))
        {
            facts = await repo.GetRunnerFactsAsync(configured, ct).ConfigureAwait(false) ?? [];
            state.FactsCache[cacheKey] = facts;
        }
        if (facts.Count == 0) return null;

        var capability = deploymentStage ? AgentCapabilities.Deployment : AgentCapabilities.PipelineBuild;
        if (facts.Any(runner => runner.AgentProtocolVersion is { } protocol
                && AgentProtocol.IsSupported(protocol)
                && runner.AgentCapabilities.Contains(capability, StringComparer.Ordinal)))
            return null;

        var details = facts.Select(runner => runner.AgentProtocolVersion is not { } protocol
                || !AgentProtocol.IsSupported(protocol)
            ? $"'{runner.Name}' reports agent protocol {runner.AgentProtocolVersion?.ToString(CultureInfo.InvariantCulture) ?? "none"}, "
              + $"outside the supported {AgentProtocol.MinimumSupportedVersion}-{AgentProtocol.MaximumSupportedVersion}"
            : $"'{runner.Name}' does not advertise {capability}");
        return $"Stage '{stage.Name}': no configured {(deploymentStage ? "deployment target" : "runner")} can take it ("
            + string.Join("; ", details)
            + "), so the stage would wait for a runner forever. Update or reinstall the agent from its server page.";
    }

    /// <summary>The selector as a reader would name it, so two stages sharing one target produce one
    /// recorded requirement instead of two identical ones.</summary>
    private static string DescribeSelector(PipelineStageDefinition effective, bool deploymentStage)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(effective.Pool)) parts.Add($"pool '{effective.Pool}'");
        if (!string.IsNullOrEmpty(effective.Environment)) parts.Add($"environment '{effective.Environment}'");
        if (!string.IsNullOrEmpty(effective.Agent)) parts.Add($"agent '{effective.Agent}'");
        if (!string.IsNullOrEmpty(effective.Os)) parts.Add($"os '{effective.Os}'");
        if (parts.Count == 0) parts.Add("any runner");
        return (deploymentStage ? "deployment target: " : string.Empty) + string.Join(", ", parts);
    }

    /// <summary>
    /// Evaluated once per DISTINCT environment, not once per stage. The guard reloads the environment
    /// and issues a DNS lookup plus an HTTP GET for every required check, each with its own timeout
    /// budget, so five stages targeting one environment with two 30 s checks could add minutes to the
    /// launch request. The environment's verdict is the same for every stage that names it.
    /// </summary>
    private async Task CheckEnvironmentAdmitsTheRunAsync(
        IGrouping<string, PipelineStageDefinition> environmentStages,
        int? projectId,
        List<string> problems,
        ChainPreflightState state,
        CancellationToken ct)
    {
        // Same guard the dispatcher uses, so the answer here and an hour later are the same answer.
        if (await environmentChecks.CheckEnvironmentChecksAsync(environmentStages.First(), projectId, ct)
                .ConfigureAwait(false))
        {
            state.Record(PreflightCheckKinds.Environment, environmentStages.Key);
            return;
        }

        var stageNames = string.Join("', '", environmentStages.Select(stage => stage.Name));
        var reason = $"Stage '{stageNames}': a required check on environment '{environmentStages.Key}' refuses "
            + "the run. Fix the check before launching; it is evaluated again at dispatch.";
        problems.Add(reason);
        state.Record(PreflightCheckKinds.Environment, environmentStages.Key, satisfied: false, detail: reason);
    }

    /// <summary>
    /// A step that names another pipeline (a <c>trigger</c>, or an artifact restored from another
    /// pipeline) fails at its own stage, which in a qualification chain is hours in. The name is
    /// known at launch, so a typo or a deleted pipeline is caught at launch.
    ///
    /// The name judged is the one the step will use at dispatch, not the YAML text: a template names
    /// its pipelines through variables the extending pipeline sets
    /// (<c>artifact_source_pipeline: "$(APPLICATION_CI_PIPELINE)"</c>), and the dispatcher substitutes
    /// them before its own lookup. A name that still carries a reference once expanded depends on a
    /// value the run only produces later (a step output); it is noted, not refused. Whether that
    /// reference can be provided at all is <see cref="PipelineUnresolvedVariableGuard"/>'s decision,
    /// taken before this check, and it refuses a reference nothing can ever supply.
    /// </summary>
    private async Task CheckReferencedPipelinesExistAsync(
        PipelineYamlDefinition definition,
        IReadOnlyList<PipelineStageDefinition> stages,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int projectId,
        List<string> problems,
        ChainPreflightState state,
        CancellationToken ct)
    {
        var stillToCome = PipelineUnresolvedVariableGuard.BuildDeferredNames(definition);
        // Existence is a property of the NAME, not of the stage that references it. An orchestrator
        // naming four pipelines across thirty steps used to issue thirty queries for four answers, so
        // each distinct name is resolved once and every referencing site reuses the verdict.
        var references = stages
            .SelectMany(stage =>
            {
                var stageVariables = DispatchVariables(stage, resolvedVariables);
                return stage.Steps.SelectMany(step => ReferencedPipelines(step).Select(reference => (
                    StageName: stage.Name,
                    PipelineName: ExpandAsDispatched(reference.Name, stageVariables, stillToCome),
                    reference.What)));
            })
            .ToList();

        var verdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in references.Select(reference => reference.PipelineName)
                     .Where(IsKnownAtLaunch)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = await repo.FindPipelineByNameAndProjectAsync(name, projectId, ct).ConfigureAwait(false);
            verdicts[name] = target is not null;
        }

        foreach (var (stageName, name, what) in references)
        {
            if (!IsKnownAtLaunch(name))
            {
                // Not recorded as a check: nothing was verified, and a satisfied entry would say it was.
                state.Warnings.Add(
                    $"Stage '{stageName}': {what} names pipeline '{name}', whose value is only known once "
                    + "the run is under way; its existence is checked when the step runs.");
                continue;
            }
            if (verdicts[name])
            {
                state.Record(PreflightCheckKinds.PipelineReference, name);
                continue;
            }
            var reason = $"Stage '{stageName}': {what} names pipeline '{name}', which does not exist in this project.";
            problems.Add(reason);
            state.Record(PreflightCheckKinds.PipelineReference, name, satisfied: false, detail: reason);
        }
    }

    /// <summary>The variables a step of this stage is dispatched with: the run's, overridden by the
    /// stage's own <c>variables:</c>, as <c>PipelineStepTaskDispatcher</c> builds them.</summary>
    private static IReadOnlyDictionary<string, string> DispatchVariables(
        PipelineStageDefinition stage, IReadOnlyDictionary<string, string> resolvedVariables)
    {
        if (stage.Variables.Count == 0) return resolvedVariables;
        var stageVariables = new Dictionary<string, string>(resolvedVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in stage.Variables) stageVariables[key] = value;
        return stageVariables;
    }

    /// <summary>
    /// The substitution <c>PipelineTriggerStepCoordinator</c> and <c>PipelineArtifactTaskFactory</c>
    /// apply to these fields (<see cref="PipelineRunHelpers.SubstituteVariables"/>). One difference,
    /// on purpose: a <c>$(NAME:-default)</c> whose NAME the run may still provide keeps its reference
    /// instead of taking the default, because at dispatch that NAME may hold another pipeline's name.
    /// </summary>
    private static string ExpandAsDispatched(
        string rawName, IReadOnlyDictionary<string, string> variables, IReadOnlySet<string> stillToCome)
        => PipelineCommandBuilder.Substitute(rawName, variables, name => !stillToCome.Contains(name)).Trim();

    /// <summary>A name the launch can judge. An empty expansion counts as unknown too: a child
    /// pipeline is preflighted with the names its parent forwards seeded empty, as placeholders.</summary>
    private static bool IsKnownAtLaunch(string expandedName)
        => expandedName.Length > 0 && !expandedName.Contains("$(", StringComparison.Ordinal);

    private static IEnumerable<(string Name, string What)> ReferencedPipelines(PipelineStepDefinition step)
    {
        if (!string.IsNullOrWhiteSpace(step.Pipeline))
            yield return (step.Pipeline.Trim(), $"step '{step.Name}'");
        if (!string.IsNullOrWhiteSpace(step.ArtifactSourcePipeline))
            yield return (step.ArtifactSourcePipeline.Trim(), $"step '{step.Name}' (artifact source)");
    }
}
