// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Routing;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Tracks the project the user is currently viewing through a project-scoped detail page
/// (vault, release, pipeline, variable library, …) so the NavMenu can keep that project's
/// submenu open even when the URL doesn't include <c>/projects/{id}</c>.
/// Cleared automatically on navigation away from any project-scoped page.
/// </summary>
public sealed class ProjectNavContextService : IDisposable
{
    private static readonly string[] TrackedPrefixes =
    [
        "projects/", "vaults/", "releases/", "pipelines/", "variable-libraries/", "environments/", "git-repositories", "artifacts/"
    ];

    private readonly NavigationManager _nav;
    private int? _projectId;

    public event Action? OnChanged;

    public ProjectNavContextService(NavigationManager nav)
    {
        _nav = nav;
        _nav.LocationChanged += OnLocationChanged;
    }

    public int? ProjectId => _projectId;

    public void Set(int? projectId)
    {
        if (_projectId == projectId) return;
        _projectId = projectId;
        OnChanged?.Invoke();
    }

    public void Clear() => Set(null);

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var rel = _nav.ToBaseRelativePath(_nav.Uri).TrimStart('/');
        if (!TrackedPrefixes.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            Clear();
    }

    public void Dispose() => _nav.LocationChanged -= OnLocationChanged;
}
