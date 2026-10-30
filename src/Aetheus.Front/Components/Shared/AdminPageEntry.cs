// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// How an administration page opens: a caller who is not an administrator is sent home, an
/// administrator gets the "Administration / page" breadcrumb. One rule for every such page.
/// </summary>
internal static class AdminPageEntry
{
    /// <summary>False when the caller was sent away: the page must stop initializing.</summary>
    public static bool TryEnter(
        AuthStateProvider auth,
        NavigationManager navigation,
        BreadcrumbService breadcrumb,
        IStringLocalizer<AppStrings> text,
        string titleKey) =>
        Enter(auth.IsAdmin, navigation, breadcrumb, text, titleKey);

    /// <summary>
    /// A page of the Administration section an organization's administrator also opens (owner decision
    /// 2026-10-28): the site Admin, or a holder of <paramref name="organizationKey"/> on one of its
    /// organizations. False when the caller was sent away.
    /// </summary>
    public static bool TryEnter(
        AuthStateProvider auth,
        PermissionService permissions,
        string organizationKey,
        NavigationManager navigation,
        BreadcrumbService breadcrumb,
        IStringLocalizer<AppStrings> text,
        string titleKey) =>
        Enter(auth.IsAdmin || permissions.Can(organizationKey), navigation, breadcrumb, text, titleKey);

    private static bool Enter(
        bool allowed, NavigationManager navigation, BreadcrumbService breadcrumb, IStringLocalizer<AppStrings> text, string titleKey)
    {
        if (!allowed)
        {
            navigation.NavigateTo("/");
            return false;
        }

        breadcrumb.Set(
            new BreadcrumbItem(text["Administration"], "/admin"),
            new BreadcrumbItem(text[titleKey]));
        return true;
    }
}
