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
/// matrix keys, and the names the dispatcher injects itself. A template that cannot be read is left
/// to the dispatch, which fails its step with the precise reason.
/// </para>
/// </summary>
public interface IPipelineConfigTemplatePreflight
{
    /// <summary>One problem per template that uses undefined tokens; empty when every token resolves
    /// or when the run has no immutable project revision to read templates at.</summary>
    Task<IReadOnlyList<string>> FindProblemsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> launchVariables,
        int? projectId,
        string? definitionCommit,
        int? sourceRepositoryId,
        CancellationToken ct);
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

    public async Task<IReadOnlyList<string>> FindProblemsAsync(
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
            return [];

        var templates = CollectTemplates(definition);
        if (templates.Count == 0) return [];

        var known = BuildKnownNames(definition, launchVariables);
        var problems = new List<string>();
        foreach (var (path, usesConfigRepository, steps) in templates)
        {
            var template = usesConfigRepository
                ? await configRepository.ReadAsync(project, path, definitionCommit, sourceRepositoryId, ct).ConfigureAwait(false)
                : await pipelineGit.ReadProjectConfigAtRevisionAsync(project, path, definitionCommit!, ct, sourceRepositoryId).ConfigureAwait(false);
            if (template is null) continue;

            var undefined = PipelineConfigTemplateRenderer.FindTokenNames(template)
                .Where(name => !known.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (undefined.Count == 0) continue;
            problems.Add(
                $"Configuration template '{path}' (used by {string.Join(", ", steps.Select(step => $"step '{step}'"))}) "
                + $"uses variable(s) {string.Join(", ", undefined.Select(name => $"'{name}'"))} that nothing in this run defines: "
                + "add them to a Variable Library the pipeline lists, or remove them from the template.");
        }
        return problems;
    }

    private static List<(string Path, bool UsesConfigRepository, SortedSet<string> Steps)> CollectTemplates(
        PipelineYamlDefinition definition)
    {
        var byKey = new Dictionary<(string, bool), SortedSet<string>>();
        foreach (var step in YamlParsingHelper.FlattenJobs(definition).SelectMany(stage => stage.Steps))
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
                if (!byKey.TryGetValue((path, usesConfigRepository), out var steps))
                    byKey[(path, usesConfigRepository)] = steps = new SortedSet<string>(StringComparer.Ordinal);
                steps.Add(step.Name);
            }
        }
        return byKey.Select(entry => (entry.Key.Item1, entry.Key.Item2, entry.Value)).ToList();
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
