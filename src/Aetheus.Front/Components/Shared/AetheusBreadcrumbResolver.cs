// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The trail OE's <see cref="OmniBreadcrumbService"/> installs on each change of path, before the page
/// names its own ancestors: the route-derived fallback of <see cref="BreadcrumbRouteResolver"/>.
/// </summary>
public sealed class AetheusBreadcrumbResolver(IStringLocalizer<AppStrings> localizer) : IOmniBreadcrumbResolver
{
    public IReadOnlyList<OmniBreadcrumbEntry> Resolve(string relativePath) =>
        [.. BreadcrumbRouteResolver.Resolve(relativePath, key => localizer[key])
            .Select(item => new OmniBreadcrumbEntry(item.Text, item.Href, item.IsLoading))];
}
