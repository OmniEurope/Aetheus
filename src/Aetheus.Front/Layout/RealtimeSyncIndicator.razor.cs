// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Layout;

/// <summary>
/// Shows "synchronising" while <see cref="RealtimeSyncStatus"/> says the page, back in the foreground,
/// is still catching up on its realtime data, and feeds that status the page's visibility changes.
/// Hidden whenever the connection-lost overlay is shown: that overlay is the authority on a real outage,
/// and the two must never be on screen together.
/// </summary>
public partial class RealtimeSyncIndicator : IAsyncDisposable
{
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>Whether the layout's connection-lost overlay is currently shown.</summary>
    [Parameter] public bool OfflineOverlayShown { get; set; }

    private DotNetObjectReference<RealtimeSyncIndicator>? _selfRef;
    private RealtimeSyncStatus Status => HubFactory.SyncStatus;

    internal bool Visible => Status.IsSyncing && !OfflineOverlayShown;

    protected override void OnInitialized() => Status.Changed += OnStatusChanged;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _selfRef = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("Aetheus.watchPageVisibility", _selfRef);
    }

    /// <summary>Called by <c>Aetheus.watchPageVisibility</c> each time the page is hidden or shown.</summary>
    [JSInvokable]
    public void OnPageVisibilityChanged(bool visible)
    {
        if (visible)
        {
            HubFactory.PageVisibility.Visible();
            Status.PageVisible();
        }
        else
        {
            HubFactory.PageVisibility.Hidden();
            Status.PageHidden();
        }
    }

    private void OnStatusChanged() => _ = InvokeAsync(StateHasChanged);

    public async ValueTask DisposeAsync()
    {
        Status.Changed -= OnStatusChanged;
        try
        {
            await JS.InvokeVoidAsync("Aetheus.disposePageVisibilityWatcher");
        }
        catch (JSDisconnectedException)
        {
            // The browser runtime is already gone; there is no watcher left to detach.
        }
        finally
        {
            _selfRef?.Dispose();
            _selfRef = null;
        }
    }
}
