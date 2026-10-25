// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The logical parent of a page: where "Back" leads when the browser holds no earlier page of the
/// application (recette R3-29). One answer for the application's back button and for the parent entry
/// put into the browser history when the application starts on a pasted or typed address. It is the
/// nearest ancestor of the breadcrumb that is a link to another page; a page without such an ancestor
/// falls back to the dashboard. The dashboard and the sign-in screens have no parent.
/// </summary>
internal static class LogicalParentRoute
{
    private const string Dashboard = "/";

    /// <summary>The parent of <paramref name="relativePath"/>, read on its route-derived trail.</summary>
    public static string? Of(string relativePath) =>
        Of(relativePath, BreadcrumbRouteResolver.Resolve(relativePath, key => key));

    /// <summary>
    /// The parent of <paramref name="relativePath"/>, read on <paramref name="trail"/>: the trail a page
    /// installed once its data is loaded, or the route-derived one. The last item is the page itself.
    /// </summary>
    public static string? Of(string relativePath, IReadOnlyList<BreadcrumbItem> trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        var current = Normalize(relativePath);
        if (IsSignInPath(current)) return null;

        for (var index = trail.Count - 2; index >= 0; index--)
        {
            var href = trail[index].Href;
            if (!string.IsNullOrWhiteSpace(href) && LoginRedirect.IsSafe(href)
                && !string.Equals(Normalize(href), current, StringComparison.OrdinalIgnoreCase))
                return href;
        }

        return current.Length == 0 ? null : Dashboard;
    }

    private static bool IsSignInPath(string normalized)
    {
        var path = "/" + normalized.Split('?')[0];
        return path.Equals(LoginRedirect.LoginPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(LoginRedirect.ChangePasswordPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path and query without the fragment and the slashes at both ends.</summary>
    private static string Normalize(string path)
    {
        var cut = path.IndexOf('#', StringComparison.Ordinal);
        if (cut >= 0) path = path[..cut];
        var query = path.IndexOf('?', StringComparison.Ordinal);
        return query >= 0
            ? path[..query].Trim('/') + path[query..]
            : path.Trim('/');
    }
}
