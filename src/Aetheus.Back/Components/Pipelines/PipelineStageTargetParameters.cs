// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Reads the run parameters a stage's target is built from (<c>agent: "${{ parameters.deployServer }}"</c>)
/// for the launch preview.
///
/// The launch substitutes those expressions in the YAML before any stage is dispatched, and a declared
/// parameter with no value becomes an empty string. The preview reads the definition before that, so it
/// must do the same, otherwise it searches for an agent literally named "${{ parameters.deployServer }}"
/// and reports it as offline.
/// </summary>
internal static class PipelineStageTargetParameters
{
    /// <summary>The stage with its targets substituted, the declared parameter that left its target empty
    /// (or null), and whether a target still holds an expression nothing could resolve.</summary>
    internal readonly record struct Resolution(
        PipelineStageDefinition Stage,
        PipelineTemplateParameterDefinition? EmptyParameter,
        bool HasUnresolvedExpression);

    internal static Resolution Resolve(
        PipelineStageDefinition stage,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyList<PipelineTemplateParameterDefinition> declared)
    {
        var pool = Substitute(stage.Pool, variables);
        var environment = Substitute(stage.Environment, variables);
        var agent = Substitute(stage.Agent, variables);
        var effective = stage with { Pool = pool, Environment = environment, Agent = agent ?? string.Empty };

        var unresolved = HasExpression(pool) || HasExpression(environment) || HasExpression(agent);
        var hasSelector = !string.IsNullOrWhiteSpace(pool) || !string.IsNullOrWhiteSpace(environment)
            || !string.IsNullOrWhiteSpace(agent);
        if (unresolved || hasSelector)
            return new Resolution(effective, null, unresolved);

        // Nothing left to select on: the target was built from parameters, so the first one it names
        // (in selector precedence) that is declared is the one with no value.
        var named = PipelineCommandBuilder.ReferencedParameterNames(stage.Pool)
            .Concat(PipelineCommandBuilder.ReferencedParameterNames(stage.Environment))
            .Concat(PipelineCommandBuilder.ReferencedParameterNames(stage.Agent));
        var empty = named
            .Select(name => declared.FirstOrDefault(
                parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(parameter => parameter is not null);
        return new Resolution(effective, empty, false);
    }

    /// <summary>
    /// R-20: the stages of the DECLARED definition (parameters not yet substituted) whose target is built
    /// from a declared parameter that holds no value, with that parameter. Read at the real launch, where
    /// the substituted definition can no longer tell an empty parameter from a stage that never named a
    /// target (both read as "no selector" and fall back to any online runner at dispatch).
    /// </summary>
    internal static List<(PipelineStageDefinition Stage, PipelineTemplateParameterDefinition Parameter)> FindEmptyTargets(
        PipelineYamlDefinition declaredDefinition,
        IReadOnlyDictionary<string, string> launchVariables,
        IReadOnlyDictionary<string, string> effectiveParameters)
    {
        // Same reading as the launch: a declared parameter with no value is an empty ${{ parameters.X }}.
        var variables = new Dictionary<string, string>(launchVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in declaredDefinition.Parameters.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
            variables[PipelineCommandBuilder.ParameterKeyPrefix + parameter.Name] =
                effectiveParameters.GetValueOrDefault(parameter.Name) ?? string.Empty;

        var empty = new List<(PipelineStageDefinition, PipelineTemplateParameterDefinition)>();
        foreach (var stage in YamlParsingHelper.FlattenJobs(declaredDefinition))
            if (Resolve(stage, variables, declaredDefinition.Parameters).EmptyParameter is { } parameter)
                empty.Add((stage, parameter));
        return empty;
    }

    /// <summary>R-23: the stage of a pipeline the launch will chain to (an <c>on_success</c> or trigger target), whose
    /// definition is read before its own launch substitutes its parameters, with the targets built from those parameters
    /// substituted from <paramref name="variables"/> (<c>parameters.X</c> keys). Only the <c>${{ parameters.X }}</c>
    /// expressions are read: the stage of the pipeline being launched was substituted already, so it is unchanged.</summary>
    internal static PipelineStageDefinition WithParameterTargets(
        PipelineStageDefinition stage, IReadOnlyDictionary<string, string> variables) =>
        HasExpression(stage.Pool) || HasExpression(stage.Environment) || HasExpression(stage.Agent)
            ? stage with
            {
                Pool = SubstituteParameters(stage.Pool, variables),
                Environment = SubstituteParameters(stage.Environment, variables),
                Agent = SubstituteParameters(stage.Agent, variables) ?? string.Empty
            }
            : stage;

    private static string? SubstituteParameters(string? selector, IReadOnlyDictionary<string, string> variables) =>
        HasExpression(selector) ? Substitute(selector, variables) : selector;

    private static string? Substitute(string? selector, IReadOnlyDictionary<string, string> variables) =>
        string.IsNullOrEmpty(selector) ? selector : PipelineCommandBuilder.Substitute(selector, variables).Trim();

    private static bool HasExpression(string? selector) =>
        selector?.Contains("${{", StringComparison.Ordinal) == true;
}
