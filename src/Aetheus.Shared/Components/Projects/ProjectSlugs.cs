// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Shared.Components.Projects;

/// <summary>
/// A project's slug and the <c>${{ project.slug }}</c> expression of library and vault names (recette R3-67), shared
/// by the back (every run's resolution) and the front (R-37: the names a run page shows are the ones the run read).
/// </summary>
public static partial class ProjectSlugs
{
    private const int MaxSlugLength = 90;

    /// <summary>
    /// The project's name in lower case, every run of characters outside <c>a-z0-9</c> replaced by
    /// one hyphen, trimmed and bounded to 90 characters; <c>project-{id}</c> when nothing is left.
    /// Never the display name as written: "Mon Aetheus" is <c>mon-aetheus</c>.
    /// </summary>
    public static string Of(string projectName, int projectId)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        var slug = NonSlugCharacters().Replace(projectName.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].Trim('-');
        return slug.Length == 0 ? $"project-{projectId}" : slug;
    }

    /// <summary><paramref name="name"/> with <c>${{ project.slug }}</c> replaced by <paramref name="slug"/>; unchanged
    /// without a slug.</summary>
    public static string Expand(string name, string? slug) =>
        slug is null || string.IsNullOrEmpty(name) ? name : SlugExpression().Replace(name, slug);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();

    [GeneratedRegex(@"\$\{\{\s*project\.slug\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex SlugExpression();
}
