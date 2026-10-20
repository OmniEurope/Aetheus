// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Shared;

/// <summary>
/// The prefix of the resources a project owns by convention: <c>&lt;slug&gt;.analytics</c> for its
/// analytics vault, <c>&lt;slug&gt;.share</c> or <c>&lt;slug&gt;.prod</c> for the libraries a generic
/// pipeline template reads (recette R3-67). One calculation, so a template and the analytics vault
/// never disagree on what a project is called.
/// </summary>
public static partial class ProjectResourceNames
{
    private const int MaxSlugLength = 90;

    /// <summary>
    /// The project's name in lower case, every run of characters outside <c>a-z0-9</c> replaced by
    /// one hyphen, trimmed and bounded to 90 characters; <c>project-{id}</c> when nothing is left.
    /// Never the display name as written: "Mon Aetheus" is <c>mon-aetheus</c>.
    /// </summary>
    public static string Slug(string projectName, int projectId)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        var slug = NonSlugCharacters().Replace(projectName.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].Trim('-');
        return slug.Length == 0 ? $"project-{projectId}" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}
