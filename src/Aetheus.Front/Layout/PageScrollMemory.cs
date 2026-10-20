// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Routing;

namespace Aetheus.Front.Layout;

/// <summary> Tells the shell's <c>Aetheus.pageScroll</c> helper (layout.js) about every navigation,
/// before it (the position of the page being left is kept) and after it (a return puts its position
/// back). Every page is covered without code of its own; the data a page shows again on a return is
/// the page's part, through <c>PageStateCache</c>. </summary>
internal sealed class PageScrollMemory : IDisposable
{
    private readonly NavigationManager _nav;
    private readonly IJSRuntime _js;
    private readonly IDisposable _leaving;

    public PageScrollMemory(NavigationManager nav, IJSRuntime js)
    {
        _nav = nav;
        _js = js;
        _leaving = nav.RegisterLocationChangingHandler(OnLeavingAsync);
        nav.LocationChanged += OnArrived;
    }

    private async ValueTask OnLeavingAsync(LocationChangingContext context)
    {
        try { await _js.InvokeVoidAsync("Aetheus.pageScroll.leaving"); }
        catch (JSException) { /* Mandatory: a scroll position never stands in the way of a navigation. */ }
    }

    private void OnArrived(object? sender, LocationChangedEventArgs e) => _ = ArrivedAsync();

    private async Task ArrivedAsync()
    {
        try { await _js.InvokeVoidAsync("Aetheus.pageScroll.arrived"); }
        catch (JSException) { /* The page is shown either way; only its former scroll position is not put back. */ }
    }

    public void Dispose()
    {
        _nav.LocationChanged -= OnArrived;
        _leaving.Dispose();
    }
}
