// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Layout;

public partial class PendingApprovalsIndicator : IAsyncDisposable
{
    [Inject] private PendingApprovalsService Approvals { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private bool _popoverOpen;

    /// <summary>The count the tab's title carries; -1 until it was first written.</summary>
    private int _titleCount = -1;

    protected override async Task OnInitializedAsync()
    {
        Approvals.OnChanged += OnApprovalsChanged;
        await Approvals.EnsureStartedAsync();
    }

    // Decision of 2026-10-06: an approval waiting shows in the browser tab's title too, "(2) <page>", and
    // the title alternates with the attention message while the tab is in the background (layout.js).
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var count = Approvals.Items.Count;
        if (count == _titleCount) return;
        _titleCount = count;
        await SetTabTitleAsync(count);
    }

    private async Task SetTabTitleAsync(int count)
    {
        try
        {
            await JS.InvokeVoidAsync("Aetheus.titleAttention.set", count,
                string.Format(L["PendingApprovalsTabTitle"].Value, count));
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            // The title is a convenience: the top-bar indicator still shows the approvals.
        }
    }

    private void OnApprovalsChanged() => InvokeAsync(StateHasChanged);

    public async ValueTask DisposeAsync()
    {
        Approvals.OnChanged -= OnApprovalsChanged;
        if (_titleCount > 0) await SetTabTitleAsync(0);
    }
}
