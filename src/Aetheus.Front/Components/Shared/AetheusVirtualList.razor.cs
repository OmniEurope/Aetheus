// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Size virtual spacers through Aetheus.sizeVirtualSpacer so only visible rows render
/// while the production CSP continues to refuse inline style attributes.</summary>
public partial class AetheusVirtualList<TItem> : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>The whole list. Only the visible window is rendered.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<TItem> Items { get; set; } = [];

    /// <summary>Height of one row in pixels. Rows are assumed to share it, as <c>Virtualize</c> does.</summary>
    [Parameter] public double ItemSize { get; set; } = 21;

    /// <summary>Rows kept rendered on each side of the viewport so a fast scroll does not show a gap.</summary>
    [Parameter] public int OverscanCount { get; set; } = 20;

    [Parameter, EditorRequired] public RenderFragment<TItem> ChildContent { get; set; } = default!;

    private ElementReference _root;
    private ElementReference _topSpacer;
    private ElementReference _bottomSpacer;
    private DotNetObjectReference<AetheusVirtualList<TItem>>? _self;
    private bool _attached;

    private double _scrollTop;
    private double _viewportHeight;
    private double _appliedTop = -1;
    private double _appliedBottom = -1;

    private double RowHeight => ItemSize > 0 ? ItemSize : 1;

    /// <summary>Index of the first rendered row, overscan included.</summary>
    internal int Start
    {
        get
        {
            if (Items.Count == 0) return 0;
            var first = (int)Math.Floor(_scrollTop / RowHeight) - OverscanCount;
            return Math.Clamp(first, 0, Math.Max(0, Items.Count - 1));
        }
    }

    /// <summary>
    /// How many rows are rendered. Before the first measurement the viewport height is unknown; a
    /// first window is rendered anyway, otherwise a list that is never scrolled would stay empty.
    /// </summary>
    internal int Count
    {
        get
        {
            if (Items.Count == 0) return 0;
            var visible = _viewportHeight > 0
                ? (int)Math.Ceiling(_viewportHeight / RowHeight)
                : OverscanCount;
            var window = visible + (2 * OverscanCount);
            return Math.Clamp(window, 0, Items.Count - Start);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            try
            {
                await JS.InvokeVoidAsync("Aetheus.watchVirtualList", _root, _self);
                _attached = true;
            }
            catch (JSException)
            {
                // No viewport to watch (a test renderer, a prerender): the first window stays.
            }
        }

        await ApplySpacersAsync();
    }

    /// <summary>The scroll position of the enclosing viewport, pushed by the watcher.</summary>
    [JSInvokable]
    public Task OnViewportAsync(double scrollTop, double viewportHeight)
    {
        var start = Start;
        var count = Count;
        _scrollTop = scrollTop;
        _viewportHeight = viewportHeight;

        if (start == Start && count == Count) return Task.CompletedTask;
        return InvokeAsync(StateHasChanged);
    }

    private async ValueTask ApplySpacersAsync()
    {
        var top = Start * RowHeight;
        var bottom = Math.Max(0, (Items.Count - (Start + Count)) * RowHeight);
        if (Math.Abs(top - _appliedTop) < 0.5 && Math.Abs(bottom - _appliedBottom) < 0.5) return;

        try
        {
            await JS.InvokeVoidAsync("Aetheus.sizeVirtualSpacer", _topSpacer, top);
            await JS.InvokeVoidAsync("Aetheus.sizeVirtualSpacer", _bottomSpacer, bottom);
            _appliedTop = top;
            _appliedBottom = bottom;
        }
        catch (JSException)
        {
            // Same as above: without JS the list simply renders its first window.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_attached)
        {
            try { await JS.InvokeVoidAsync("Aetheus.unwatchVirtualList", _root); }
            catch (JSException) { /* the page is already gone */ }
            catch (JSDisconnectedException) { /* the circuit is already gone */ }
        }

        _self?.Dispose();
        _self = null;
    }
}
