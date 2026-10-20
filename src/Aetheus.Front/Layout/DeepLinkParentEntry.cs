// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Layout;

/// <summary>
/// Recette R3-29: an address pasted or typed into the browser loads the application on that page
/// alone, so the browser's Back would leave the application. When the signed-in application is first
/// shown (MainLayout, as its start-up splash goes), the page's logical parent
/// (<see cref="LogicalParentRoute"/>) is put into the history just before it; Back then reaches the
/// parent through the Blazor router, without a reload. <c>Aetheus.seedParentHistoryEntry</c>
/// (layout.js) acts once per document and refuses whenever the document was not opened on that
/// address: a reload, a return through the history, a page already left inside the application, a
/// full reload the application asked for itself.
/// </summary>
internal static class DeepLinkParentEntry
{
    /// <summary>Offers the parent entry of the current page to the browser history.</summary>
    /// <returns>Whether the browser history received the parent entry.</returns>
    public static async Task<bool> SeedAsync(NavigationManager nav, IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(nav);
        ArgumentNullException.ThrowIfNull(js);
        var parent = LogicalParentRoute.Of(nav.ToBaseRelativePath(nav.Uri));
        if (parent is null) return false;
        return await js.InvokeAsync<bool>("Aetheus.seedParentHistoryEntry", nav.ToAbsoluteUri(parent).ToString());
    }
}
