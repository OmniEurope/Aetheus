// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The trail OE's <see cref="OmniBreadcrumbService"/> installs on each change of path, before the page
/// names its own ancestors: the route-derived fallback of <see cref="BreadcrumbRouteResolver"/>.
/// </summary>
/// <remarks>
/// The project crumb of a <c>/projects/{id}/...</c> path carries the name of the project the shared
/// <see cref="ProjectDetailLoader"/> already holds. The router mounts <c>ProjectDetailLayout</c> inside the
/// navigation event, before OE's service handles that same event and installs this fallback; coming from a
/// project page outside the layout (the standalone settings page), the layout's own naming ran first and the
/// loader, already loaded, raises no later change, so a nameless fallback left the crumb a skeleton for good.
/// </remarks>
public sealed class AetheusBreadcrumbResolver(IStringLocalizer<AppStrings> localizer, ProjectDetailLoader projects)
    : IOmniBreadcrumbResolver
{
    public IReadOnlyList<OmniBreadcrumbEntry> Resolve(string relativePath)
    {
        var items = BreadcrumbRouteResolver.Resolve(relativePath, key => localizer[key]).ToArray();
        if (items.Length > 1 && projects.Project is { } project && IsPathOfProject(relativePath, project.Id))
            items[1] = items[1] with { Text = project.Name, IsLoading = false };
        return [.. items.Select(item => new OmniBreadcrumbEntry(item.Text, item.Href, item.IsLoading))];
    }

    private static bool IsPathOfProject(string relativePath, int projectId)
    {
        var segments = relativePath.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1
            && segments[0].Equals("projects", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            && id == projectId;
    }
}
