// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-36: the <c>on_success:</c> follow-ups that a chained launch would refuse because the follower declares a required
/// parameter with no default that the edge does not supply, read before any run. A chained launch gives the follower the
/// upstream variables and, when the trigger names a release and the follower declares <c>candidateVersion</c>, that
/// release as <c>candidateVersion</c>: nothing else (the trigger model carries no parameter values). The
/// <c>deployServer</c> parameter has its own reading (<see cref="PipelineChainedDeployServer"/>), which also knows the
/// launch picks the project's only server, so it is not repeated here.
/// </summary>
internal static class PipelineChainedRequiredParameters
{
    private const string CandidateVersion = "candidateVersion";

    /// <summary>One <c>upstream &gt; follower &gt; parameter</c> per required parameter of a follower the project holds
    /// that has neither a default nor a value from the edge. A follower the project does not hold is the launch's own other
    /// refusal, not this one. Order follows the upstream pipelines, then their triggers, then the declared parameters.</summary>
    public static List<string> FindUnsupplied(
        IEnumerable<(string Name, PipelineYamlDefinition Definition)> upstream,
        IReadOnlyDictionary<string, PipelineYamlDefinition> targets)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(targets);
        var missing = new List<string>();
        foreach (var (name, definition) in upstream)
            foreach (var trigger in definition.OnSuccess)
            {
                var targetName = trigger.Pipeline?.Trim() ?? string.Empty;
                if (!targets.TryGetValue(targetName, out var target)) continue;
                var releaseSupplied = !string.IsNullOrWhiteSpace(trigger.Release);
                missing.AddRange(target.Parameters
                    .Where(parameter => IsUnsupplied(parameter, releaseSupplied))
                    .Select(parameter => $"{name} > {targetName} > {parameter.Name}"));
            }
        return [.. missing.Distinct(StringComparer.Ordinal)];
    }

    private static bool IsUnsupplied(PipelineTemplateParameterDefinition parameter, bool releaseSupplied) =>
        parameter is { Required: true }
        && !string.IsNullOrWhiteSpace(parameter.Name)
        && string.IsNullOrWhiteSpace(parameter.Default)
        && !string.Equals(parameter.Name, PipelineChainedDeployServer.ParameterName, StringComparison.OrdinalIgnoreCase)
        && !(releaseSupplied && string.Equals(parameter.Name, CandidateVersion, StringComparison.OrdinalIgnoreCase));
}
