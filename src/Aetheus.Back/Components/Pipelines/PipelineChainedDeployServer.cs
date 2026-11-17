// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-36: a run started by an upstream <c>on_success:</c> receives no parameter but <c>candidateVersion</c>, so the
/// <c>deployServer</c> parameter of a deployment pipeline must carry a default. A pipeline created before the setup
/// wizard required that choice may not. This is the safety net at chained launch only: with exactly one server in the
/// project the choice is not ambiguous and that server is used; with several (or none) nothing is guessed, and the
/// launch is refused saying which parameter is missing and how to fix it.
/// </summary>
internal static class PipelineChainedDeployServer
{
    public const string ParameterName = "deployServer";

    /// <summary>What a chained launch does about <c>deployServer</c>: nothing to do, use the only server, or refuse.</summary>
    internal enum Branch { NotApplicable, OnlyServer, Refuse }

    /// <summary>Whether the declared <c>deployServer</c> parameter would make the launch fail: it is required and has
    /// neither a value supplied nor a default.</summary>
    public static bool IsMissing(
        IReadOnlyList<PipelineTemplateParameterDefinition>? declared, IReadOnlyDictionary<string, string>? supplied) =>
        declared?.FirstOrDefault(p => string.Equals(p.Name, ParameterName, StringComparison.OrdinalIgnoreCase)) is { Required: true } parameter
        && string.IsNullOrWhiteSpace(parameter.Default)
        && !(supplied?.TryGetValue(ParameterName, out var value) == true && !string.IsNullOrWhiteSpace(value));

    /// <summary>The branch taken for a missing <c>deployServer</c> given the names of the project's servers. The only
    /// server is used only when the parameter accepts it (a <c>choice</c> lists its allowed values).</summary>
    public static Branch Decide(
        IReadOnlyList<PipelineTemplateParameterDefinition> declared, IReadOnlyDictionary<string, string>? supplied,
        IReadOnlyList<string> projectServers)
    {
        if (!IsMissing(declared, supplied)) return Branch.NotApplicable;
        if (projectServers.Count != 1) return Branch.Refuse;
        var parameter = declared.First(p => string.Equals(p.Name, ParameterName, StringComparison.OrdinalIgnoreCase));
        return parameter.AllowedValues.Count == 0 || parameter.AllowedValues.Contains(projectServers[0], StringComparer.Ordinal)
            ? Branch.OnlyServer
            : Branch.Refuse;
    }

    /// <summary>
    /// R-36: the <c>on_success:</c> follow-ups that a chained launch would refuse for the missing <c>deployServer</c>, read
    /// before any run (the same <see cref="Decide"/> the launch takes, with only the release supplied as it is). Each is
    /// <c>upstream &gt; target</c>; a target the project does not hold is the launch's own other refusal, not this one.
    /// </summary>
    public static List<string> FindRefusedFollowUps(
        IEnumerable<(string Name, PipelineYamlDefinition Definition)> upstream,
        IReadOnlyDictionary<string, PipelineYamlDefinition> targets, IReadOnlyList<string> projectServers)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(projectServers);
        var refused = new List<string>();
        foreach (var (name, definition) in upstream)
            foreach (var trigger in definition.OnSuccess)
            {
                var targetName = trigger.Pipeline?.Trim() ?? string.Empty;
                if (targets.TryGetValue(targetName, out var target)
                    && Decide(target.Parameters, supplied: null, projectServers) == Branch.Refuse)
                    refused.Add($"{name} > {targetName}");
            }
        return refused;
    }

    public static PipelineProblem MissingProblem(string pipelineName, int serverCount) => PipelineProblem.Of(
        PipelineProblemCodes.ChainedDeployServerMissing,
        $"parameter '{ParameterName}' has no value and no default, and the project has {serverCount} servers, so none is chosen " +
        $"automatically: set the default of '{ParameterName}' in pipeline '{pipelineName}' (or launch it by hand and pick the server)",
        ("parameter", ParameterName), ("pipeline", pipelineName), ("servers", serverCount));
}
