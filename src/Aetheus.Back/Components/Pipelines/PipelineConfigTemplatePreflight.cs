// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-32: reads, at launch, every versioned configuration template the run's <c>apache-config</c> and
/// <c>apache-proxy</c> steps will render and refuses the launch when one of them uses a <c>#{NAME}#</c>
/// token nothing in the run can ever define.
/// <para>
/// <see cref="PipelineConfigTemplateRenderer.RenderStrict"/> already refuses such a token, but only when
/// the stage that renders it is dispatched: aetheus-demo had by then run "Reset demo" and the demo site
/// stayed down. Refusing before the run exists keeps every stage, destructive ones included, from
/// running at all.
/// </para>
/// <para>
/// It only anticipates the dispatch-time refusal and never replaces it. A name is accepted when the
/// run can still provide it: the launch variables, the control-plane, parameter and step-output names
/// <see cref="PipelineUnresolvedVariableGuard.BuildDeferredNames"/> knows, any stage's variables and
/// matrix keys, and the names the dispatcher injects itself.
/// </para>
/// <para>
/// R-82: a template that cannot be read is a refusal too. The dispatch reads it with the very same call,
/// at the same immutable revision, so a null here is the null the step would meet - but only after the
/// stages before it had already changed the host (the ACME directory, the HTTP vhost), with the
/// rollback skipped. It refuses when a step certain to run needs it. A step whose condition depends on
/// a value the run only knows later (a step output, a control-plane variable) can only be warned about.
/// </para>
/// </summary>
public interface IPipelineConfigTemplatePreflight
{
    /// <summary>The launch refusals and the nonblocking observations about the templates the run renders.
    /// Empty when every template is readable and every token resolves, or when the run has no immutable
    /// project revision to read templates at.</summary>
    Task<PipelineConfigTemplateFindings> FindProblemsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> launchVariables,
        int? projectId,
        string? definitionCommit,
        int? sourceRepositoryId,
        CancellationToken ct);
}

/// <summary><see cref="Problems"/> refuse the launch; <see cref="Warnings"/> are kept on the run.</summary>
public sealed record PipelineConfigTemplateFindings(
    IReadOnlyList<PipelineProblem> Problems, IReadOnlyList<PipelineProblem> Warnings)
{
    public static PipelineConfigTemplateFindings None { get; } = new([], []);
}

internal sealed class PipelineConfigTemplatePreflight(
    IPipelineGitService pipelineGit,
    IProjectConfigRepositoryService configRepository) : IPipelineConfigTemplatePreflight
{
    /// <summary>Set by the dispatcher on the stage or step variables, never by a library.</summary>
    private static readonly string[] InjectedAtDispatch =
    [
        "AETHEUS_EXECUTION_ROLE", PipelineRunService.HostWorkspaceVariable,
        "AETHEUS_APACHE_SERVER_NAME", "AETHEUS_APACHE_UPSTREAM",
        .. AppDeployEnvProvider.DerivedNames
    ];

    public async Task<PipelineConfigTemplateFindings> FindProblemsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> launchVariables,
        int? projectId,
        string? definitionCommit,
        int? sourceRepositoryId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(launchVariables);
        // Without an immutable project revision the dispatch refuses these steps itself.
        if (projectId is not { } project || !PipelineRunHelpers.IsGitCommitHash(definitionCommit))
            return PipelineConfigTemplateFindings.None;

        var templates = CollectTemplates(definition, launchVariables);
        if (templates.Count == 0) return PipelineConfigTemplateFindings.None;

        var known = BuildKnownNames(definition, launchVariables);
        var problems = new List<PipelineProblem>();
        var warnings = new List<PipelineProblem>();
        foreach (var use in templates)
        {
            var template = use.UsesConfigRepository
                ? await configRepository.ReadAsync(project, use.Path, definitionCommit, sourceRepositoryId, ct).ConfigureAwait(false)
                : await pipelineGit.ReadProjectConfigAtRevisionAsync(project, use.Path, definitionCommit!, ct, sourceRepositoryId).ConfigureAwait(false);
            if (template is null)
            {
                // R-82: the dispatch would meet the same null, after the stages before it had changed the host.
                if (use.CertainSteps.Count > 0)
                    problems.Add(DescribeMissing(use, use.CertainSteps, definitionCommit!, mayNotRun: false));
                else if (use.UncertainSteps.Count > 0)
                    warnings.Add(DescribeMissing(use, use.UncertainSteps, definitionCommit!, mayNotRun: true));
                continue;
            }

            var undefined = PipelineConfigTemplateRenderer.FindTokenNames(template)
                .Where(name => !known.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (undefined.Count == 0) continue;
            problems.Add(PipelineProblem.Of(
                PipelineProblemCodes.ConfigTemplateUndefinedVariables,
                $"Configuration template '{use.Path}' (used by {string.Join(", ", use.Steps.Select(step => $"step '{step}'"))}) "
                + $"uses variable(s) {string.Join(", ", undefined.Select(name => $"'{name}'"))} that nothing in this run defines: "
                + "add them to a Variable Library the pipeline lists, or remove them from the template.",
                ("path", use.Path), ("steps", PipelineProblem.QuotedList(use.Steps)),
                ("variables", PipelineProblem.QuotedList(undefined))));
        }
        return new PipelineConfigTemplateFindings(problems, warnings);
    }

    private static PipelineProblem DescribeMissing(TemplateUse use, IEnumerable<string> steps, string commit, bool mayNotRun)
    {
        var stepNames = steps.ToList();
        var usedBy = string.Join(", ", stepNames.Select(step => $"step '{step}'"));
        var english = use.UsesConfigRepository
            ? $"Configuration template '{use.Path}' (used by {usedBy}) was found neither in the project's "
              + $"configuration repository nor at commit {commit} of its source: prepare the production "
              + "deployment from the pipeline setup wizard, which creates it, before launching."
            : $"Configuration template '{use.Path}' (used by {usedBy}) was not found at commit {commit} of the project's source.";
        if (mayNotRun)
            english += " The condition of the step depends on a value only known once the run is under way, "
                + "so the run is not refused.";
        var code = (use.UsesConfigRepository, mayNotRun) switch
        {
            (true, false) => PipelineProblemCodes.ConfigTemplateMissingInConfigRepository,
            (true, true) => PipelineProblemCodes.ConfigTemplateMissingInConfigRepositoryMayRun,
            (false, false) => PipelineProblemCodes.ConfigTemplateMissingAtCommit,
            (false, true) => PipelineProblemCodes.ConfigTemplateMissingAtCommitMayRun,
        };
        return PipelineProblem.Of(
            code, english, ("path", use.Path), ("steps", PipelineProblem.QuotedList(stepNames)), ("commit", commit));
    }

    /// <summary>One template path, the steps that render it, and which of them will certainly run, may run,
    /// or never run on this launch's variables.</summary>
    private sealed class TemplateUse(string path, bool usesConfigRepository)
    {
        public string Path { get; } = path;
        public bool UsesConfigRepository { get; } = usesConfigRepository;
        public SortedSet<string> Steps { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> CertainSteps { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> UncertainSteps { get; } = new(StringComparer.Ordinal);
    }

    private enum Reach { Yes, No, Unknown }

    /// <summary>Whether the step runs, from the stage and step conditions read on the launch variables.
    /// A comparison on a value the run can still change (a step output, a control-plane variable, an
    /// unresolved reference) or that the launch does not hold is Unknown, never guessed.</summary>
    private static Reach ReachOf(
        PipelineStageDefinition stage, PipelineStepDefinition step,
        IReadOnlyDictionary<string, string> launchVariables, IReadOnlySet<string> deferred)
    {
        var variables = new Dictionary<string, string>(launchVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in stage.Variables) variables[key] = value;
        var stageReach = ReachOf(stage.Condition, variables, deferred);
        var stepReach = ReachOf(step.Condition, variables, deferred);
        if (stageReach == Reach.No || stepReach == Reach.No) return Reach.No;
        return stageReach == Reach.Yes && stepReach == Reach.Yes ? Reach.Yes : Reach.Unknown;
    }

    private static Reach ReachOf(string? condition, Dictionary<string, string> variables, IReadOnlySet<string> deferred)
    {
        if (PipelineConditionEvaluator.FindComparedVariable(condition) is { } name
            && (deferred.Contains(name)
                || !variables.TryGetValue(name, out var value)
                || value.Contains("$(", StringComparison.Ordinal)))
            return Reach.Unknown;
        // failed() and cancelled() read No: a stage that only runs after a failure is not a reason to refuse.
        var verdict = PipelineConditionEvaluator.Evaluate(condition, variables, previousStageFailed: false);
        return verdict.IsInvalid || !verdict.Met ? Reach.No : Reach.Yes;
    }

    private static List<TemplateUse> CollectTemplates(
        PipelineYamlDefinition definition, IReadOnlyDictionary<string, string> launchVariables)
    {
        var deferred = PipelineUnresolvedVariableGuard.BuildDeferredNames(definition);
        var byKey = new Dictionary<(string, bool), TemplateUse>();
        foreach (var (stage, step) in YamlParsingHelper.FlattenJobs(definition)
                     .SelectMany(stage => stage.Steps.Select(step => (stage, step))))
        {
            var type = step.Type?.Trim().ToLowerInvariant();
            IEnumerable<(string Path, bool UsesConfigRepository)> paths = type switch
            {
                // The dispatcher reads config_files from the configuration repository first (ADR-057).
                "apache-config" => step.ConfigFiles.Values.Select(path => (path.Trim(), true)),
                "apache-proxy" => [(string.IsNullOrWhiteSpace(step.ConfigTemplate)
                    ? PipelineHostOperationTaskFactory.DefaultProxyTemplatePath
                    : step.ConfigTemplate.Trim(), false)],
                _ => []
            };
            foreach (var (path, usesConfigRepository) in paths)
            {
                // An unsafe path is refused by the definition validator and the dispatch, never read here.
                if (!PipelineConfigTemplateRenderer.IsSafeConfigPath(path)) continue;
                if (!byKey.TryGetValue((path, usesConfigRepository), out var use))
                    byKey[(path, usesConfigRepository)] = use = new TemplateUse(path, usesConfigRepository);
                use.Steps.Add(step.Name);
                switch (ReachOf(stage, step, launchVariables, deferred))
                {
                    case Reach.Yes: use.CertainSteps.Add(step.Name); break;
                    case Reach.Unknown: use.UncertainSteps.Add(step.Name); break;
                }
            }
        }
        return [.. byKey.Values];
    }

    private static HashSet<string> BuildKnownNames(
        PipelineYamlDefinition definition, IReadOnlyDictionary<string, string> launchVariables)
    {
        var known = PipelineUnresolvedVariableGuard.BuildDeferredNames(definition);
        known.UnionWith(launchVariables.Keys);
        known.UnionWith(definition.Variables.Keys);
        known.UnionWith(InjectedAtDispatch);
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
        {
            known.UnionWith(stage.Variables.Keys);
            if (stage.Matrix is not null)
                known.UnionWith(stage.Matrix.Keys);
        }
        return known;
    }
}
