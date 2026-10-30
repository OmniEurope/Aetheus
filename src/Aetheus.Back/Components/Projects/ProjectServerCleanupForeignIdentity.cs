// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>
/// Recette R-97 (ADR-058): what the other projects' libraries name, resolved the way this project's are
/// (<c>&lt;slug&gt;.share</c> under each library, <c>$(NAME)</c> expanded, the host formula applied), and the other
/// projects' slugs. A value of this project that another project also names (whatever the casing, whatever the
/// expression that produced it), or that is named after a slug at least as long as this project's (project
/// "shop" never claims "shop-api-prod" while a project "shop-api" exists), is never removed.
/// </summary>
internal sealed class ProjectServerCleanupForeignIdentity
{
    private readonly HashSet<string> _composeProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stateDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _upstreamConfigurations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _slugs = [];

    public static ProjectServerCleanupForeignIdentity From(IReadOnlyList<CleanupForeignProject> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var identity = new ProjectServerCleanupForeignIdentity();
        foreach (var project in projects)
        {
            // Only a project of the same organization can share this project's servers, so only its slug claims names.
            if (project.Slug.Length > 0 && project.SameOrganization) identity._slugs.Add(project.Slug);
            var share = project.Slug.Length == 0 ? null : project.Libraries.FirstOrDefault(library =>
                string.Equals(library.Name, $"{project.Slug}.share", StringComparison.OrdinalIgnoreCase));
            foreach (var library in project.Libraries)
                identity.Add(ProjectServerCleanupLibraryValues.Merge(library == share ? null : share?.Entries, library.Entries));
        }
        return identity;
    }

    /// <summary>Whether another project names <paramref name="value"/> under <paramref name="key"/>.</summary>
    public bool Claims(string slug, string key, string value)
    {
        if (ProjectServerCleanupLibraryValues.IsHostKey(key)) return _hosts.Contains(value);
        return key switch
        {
            ProjectServerCleanupLibraryValues.ComposeProjectKey => _composeProjects.Contains(value) || NamedAfterAnother(slug, value),
            ProjectServerCleanupLibraryValues.StateDirectoryKey => _stateDirectories.Contains(value)
                || NamedAfterAnother(slug, value[(value.LastIndexOf('/') + 1)..]),
            ProjectServerCleanupLibraryValues.UpstreamConfigurationKey => _upstreamConfigurations.Contains(value)
                || NamedAfterAnother(slug, ProjectServerResourceNames.UpstreamConfigurationStem(value)),
            _ => false
        };
    }

    private bool NamedAfterAnother(string slug, string name) =>
        _slugs.Any(other => other.Length >= slug.Length && ProjectServerResourceNames.IsNamedAfter(name.ToLowerInvariant(), other));

    private void Add(IReadOnlyDictionary<string, string> values)
    {
        AddResolved(_composeProjects, values.GetValueOrDefault(ProjectServerCleanupLibraryValues.ComposeProjectKey), values);
        AddResolved(_stateDirectories, values.GetValueOrDefault(ProjectServerCleanupLibraryValues.StateDirectoryKey), values);
        AddResolved(_upstreamConfigurations, values.GetValueOrDefault(ProjectServerCleanupLibraryValues.UpstreamConfigurationKey), values);
        foreach (var (_, raw) in ProjectServerCleanupLibraryValues.RawHosts(values))
            AddResolved(_hosts, raw, values);
    }

    private static void AddResolved(HashSet<string> set, string? raw, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var value = ProjectServerCleanupLibraryValues.Expand(raw, values);
        if (!string.IsNullOrEmpty(value)) set.Add(value);
    }
}
