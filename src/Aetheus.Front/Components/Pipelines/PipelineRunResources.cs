// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;
using Aetheus.Shared.Components.Projects;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-37: the variable libraries and vaults a run used. The run records the exact definition it executed
/// (<see cref="PipelineRunDto.YamlSnapshot"/>, templates already expanded), whose <c>variable_libraries:</c> and
/// <c>vaults:</c> lists, plus what <c>requires:</c> declares, are the resources the run read. Nothing else
/// records them, so they are read from that snapshot: a run created before the snapshot existed names none.
/// </summary>
public static class PipelineRunResources
{
    public sealed record Used(IReadOnlyList<string> Libraries, IReadOnlyList<string> Vaults)
    {
        public static readonly Used None = new([], []);

        public bool IsEmpty => Libraries.Count == 0 && Vaults.Count == 0;
    }

    /// <summary>The libraries and vaults the snapshot names, each name once, in declaration order, with
    /// <c>${{ project.slug }}</c> replaced by the slug of the run's project (the name the run read), unchanged without one.</summary>
    public static Used Of(string? yamlSnapshot, string? projectName = null, int? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(yamlSnapshot)) return Used.None;
        if (new YamlSerializationService().Parse(yamlSnapshot) is not { } definition) return Used.None;

        var slug = projectName is not null && projectId is { } id ? ProjectSlugs.Of(projectName, id) : null;
        return new Used(
            Distinct(definition.VariableLibraries.Concat(definition.Requires?.Libraries ?? []), slug),
            Distinct(definition.Vaults.Concat(definition.Requires?.Vaults ?? []), slug));
    }

    private static List<string> Distinct(IEnumerable<string> names, string? slug) =>
        [.. names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => ProjectSlugs.Expand(name, slug)).Distinct(StringComparer.OrdinalIgnoreCase)];
}
