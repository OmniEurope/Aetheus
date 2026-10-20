// SPDX-License-Identifier: EUPL-1.2

using Aetheus.Front.Layout;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class BrowserBackButton
{
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    [Parameter] public string? Text { get; set; }
    [Parameter] public string? Title { get; set; }
    [Parameter] public OmniControlSize Size { get; set; } = OmniControlSize.Medium;

    /// <summary>
    /// To the page's logical parent, read on the trail the page shows (R3-29): it does not depend on
    /// what the browser history holds, so a page opened on a pasted address still goes back inside the
    /// application. Only a page without a parent (the dashboard) steps back in the history.
    /// </summary>
    private async Task GoBackAsync()
    {
        if (LogicalParentRoute.Of(Nav.ToBaseRelativePath(Nav.Uri), Breadcrumb.Items) is { } parentHref)
        {
            Nav.NavigateTo(parentHref);
            return;
        }

        await Js.InvokeVoidAsync("Aetheus.goBack");
    }
}
