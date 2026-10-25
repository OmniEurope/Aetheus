// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary> The pages' breadcrumb, kept in OE's <see cref="OmniBreadcrumbService"/>, which the
/// page header (<see cref="OmniPageHeader"/>) reads for its title and its trail. Routed pages may
/// replace the route-derived fallback (<see cref="AetheusBreadcrumbResolver"/>) with entity names
/// once their data is loaded; a query-only navigation keeps the current items, a path change
/// installs the fallback of the destination at once, so the header never goes empty while a page is
/// loading. </summary>
public sealed class BreadcrumbService(
    OmniBreadcrumbService trail,
    NavigationManager nav,
    IStringLocalizer<AppStrings> localizer)
{
    public IReadOnlyList<BreadcrumbItem> Items =>
        [.. trail.Items.Select(entry => new BreadcrumbItem(entry.Text, entry.Href, entry.Loading))];

    /// <summary>
    /// Nearest clickable ancestor of the current breadcrumb item. The final item is always treated as
    /// the current page, even if a caller accidentally assigns it an href.
    /// </summary>
    public string? ParentHref => trail.ParentHref;

    public event Action? OnChanged
    {
        add => trail.Changed += value;
        remove => trail.Changed -= value;
    }

    public void Set(params BreadcrumbItem[] items) =>
        trail.Set([.. items.Select(item => new OmniBreadcrumbEntry(item.Text, item.Href, item.IsLoading))]);

    /// <summary>Puts the route-derived trail of the current page back.</summary>
    public void Clear() => trail.Reset();

    /// <summary>Without ancestors, line 2 shows <paramref name="subtitle"/> or the route
    /// description. With ancestors, line 2 holds the breadcrumb and an explicit subtitle appears
    /// below the header.</summary>
    public string? Subtitle(string? subtitle = null) =>
        !string.IsNullOrWhiteSpace(subtitle) || trail.Ancestors.Count > 0 ? subtitle : RouteDescription;

    /// <summary>The route description, keyed by the path with slashes and dashes as underscores.</summary>
    private string? RouteDescription
    {
        get
        {
            var path = nav.ToBaseRelativePath(nav.Uri);
            var cut = path.IndexOfAny(['?', '#']);
            if (cut >= 0) path = path[..cut];
            path = path.Trim('/');
            var key = path.Length == 0 ? "dashboard" : path.Replace('/', '_').Replace('-', '_').ToLowerInvariant();
            var text = localizer[$"PageDescription_{key}"];
            return text.ResourceNotFound ? null : text.Value;
        }
    }
}

public sealed record BreadcrumbItem(string Text, string? Href = null, bool IsLoading = false);
