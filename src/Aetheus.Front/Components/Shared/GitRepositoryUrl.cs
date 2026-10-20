// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public static class GitRepositoryUrl
{
    public static string? Canonicalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var canonical = value.Trim().Replace('\\', '/').TrimEnd('/');
        if (canonical.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) canonical = canonical[..^4];
        return canonical.ToLowerInvariant();
    }

    public static string? CanonicalPath(string? value)
    {
        var canonical = Canonicalize(value);
        if (canonical is null) return null;
        if (Uri.TryCreate(canonical, UriKind.Absolute, out var uri))
            return uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return canonical.StartsWith('/') ? canonical : null;
    }

    /// <summary>True for an Aetheus internal clone URL (<c>/git/{projectId}/{slug}.git</c>, with or
    /// without the suffix). It serves git over smart HTTP, not a web page, so no <c>/commit/</c> or
    /// <c>/tree/</c> link may be built on it.</summary>
    public static bool IsInternalClone(string? value) => TryParseInternalClone(value, out _, out _);

    /// <summary>The project id and slug an internal clone URL names; its host is ignored, since the
    /// backend re-homes it per environment while the path stays.</summary>
    public static bool TryParseInternalClone(string? value, out int projectId, out string slug)
    {
        projectId = 0;
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!MirrorRepoPath.TryParse(path.EndsWith(".git", StringComparison.Ordinal) ? path : $"{path}.git", out var normalized))
            return false;
        var segments = normalized.Split('/');
        projectId = int.Parse(segments[1], System.Globalization.CultureInfo.InvariantCulture);
        slug = segments[2][..^4];
        return true;
    }

    /// <summary> The one repository among <paramref name="repositories"/> a clone URL names: by
    /// project and slug for an internal clone URL, by canonical URL otherwise, and the only
    /// repository when no URL is known. Null when none or several match: never guessed. </summary>
    public static int? ResolveRepositoryId(string? repositoryUrl, IReadOnlyList<GitLightRepoDto> repositories)
    {
        List<GitLightRepoDto> matches;
        if (TryParseInternalClone(repositoryUrl, out var projectId, out var slug))
        {
            matches = repositories.Where(repository => repository.ProjectId == projectId
                && string.Equals(repository.Slug, slug, StringComparison.Ordinal)).ToList();
        }
        else if (Canonicalize(repositoryUrl) is { } expected)
        {
            matches = repositories.Where(repository => Canonicalize(repository.CloneUrl) == expected).ToList();
        }
        else
        {
            matches = [.. repositories];
        }

        return matches.Select(repository => repository.Id).Distinct().Count() == 1 ? matches[0].Id : null;
    }
}
