// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R3-67: a generic template names the libraries and vaults it reads relative to the project
/// that runs it, <c>${{ project.slug }}.share</c>, because the same template serves every project and
/// a library name is the project's own (<c>aetheus.share</c>, <c>wordmorrow.share</c>). The expression
/// is replaced by <see cref="ProjectResourceNames.Slug"/> wherever a definition meets its project: the
/// setup wizard's check and creation, the project overview's warning, the launch preflight and every
/// variable resolution of a run. One replacement, so the wizard creates exactly the name a run reads.
/// </summary>
public static partial class PipelineProjectResourceNames
{
    /// <summary>The expression as a template writes it.</summary>
    public const string SlugExpression = "${{ project.slug }}";

    [GeneratedRegex(@"\$\{\{\s*project\.slug\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"\$\{\{", RegexOptions.CultureInvariant)]
    private static partial Regex AnyExpression();

    /// <summary>A template parameter, replaced by the template resolver before the name is read.</summary>
    [GeneratedRegex(@"\$\{\{\s*parameters\.[A-Za-z_][A-Za-z0-9_]*\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterExpression();

    /// <summary>
    /// The project slug for <paramref name="projectId"/>, or null when the definition has no project
    /// (or the project no longer exists). A null slug leaves the expression in place, so the lookup
    /// fails on a name nobody can have created rather than on a guessed one.
    /// </summary>
    public static async Task<string?> FindSlugAsync(IPipelineRepository repo, int? projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (projectId is not { } id) return null;
        var name = await repo.GetProjectNameAsync(id, ct).ConfigureAwait(false);
        return name is null ? null : ProjectResourceNames.Slug(name, id);
    }

    /// <summary>Whether <paramref name="name"/> carries a template expression other than the slug or a
    /// template parameter: one nothing would replace, so the lookup could never match it.</summary>
    public static bool HasUnsupportedExpression(string name) =>
        !string.IsNullOrEmpty(name)
        && AnyExpression().IsMatch(ParameterExpression().Replace(SlugPattern().Replace(name, string.Empty), string.Empty));

    public static string Expand(string name, string? slug) =>
        slug is null || string.IsNullOrEmpty(name) ? name : SlugPattern().Replace(name, slug);

    /// <summary>
    /// The definition with every library and vault name expanded: the ones it reads
    /// (<c>variable_libraries:</c>, <c>vaults:</c>) and the ones it declares (<c>requires:</c>). The
    /// same instance when there is nothing to replace.
    /// </summary>
    public static PipelineYamlDefinition Expand(PipelineYamlDefinition definition, string? slug)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (slug is null || !UsesSlug(definition)) return definition;
        return definition with
        {
            VariableLibraries = ExpandAll(definition.VariableLibraries, slug),
            Vaults = ExpandAll(definition.Vaults, slug),
            Requires = definition.Requires is { } requires
                ? requires with
                {
                    Libraries = ExpandAll(requires.Libraries, slug),
                    Vaults = ExpandAll(requires.Vaults, slug)
                }
                : null
        };
    }

    /// <summary>Resolves the project's slug and expands <paramref name="definition"/> with it.</summary>
    public static async Task<PipelineYamlDefinition> ExpandForProjectAsync(
        PipelineYamlDefinition definition, IPipelineRepository repo, int? projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!UsesSlug(definition)) return definition;
        return Expand(definition, await FindSlugAsync(repo, projectId, ct).ConfigureAwait(false));
    }

    private static bool UsesSlug(PipelineYamlDefinition definition) =>
        definition.VariableLibraries.Concat(definition.Vaults)
            .Concat(definition.Requires?.Libraries ?? [])
            .Concat(definition.Requires?.Vaults ?? [])
            .Any(name => !string.IsNullOrEmpty(name) && SlugPattern().IsMatch(name));

    private static List<string> ExpandAll(IEnumerable<string> names, string slug) =>
        [.. names.Select(name => Expand(name, slug))];
}
