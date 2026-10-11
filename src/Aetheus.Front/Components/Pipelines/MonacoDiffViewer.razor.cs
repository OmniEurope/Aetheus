// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class MonacoDiffViewer : IAsyncDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public string OriginalValue { get; set; } = string.Empty;
    [Parameter] public string ModifiedValue { get; set; } = string.Empty;
    [Parameter] public bool IsDark { get; set; } = true;

    private string ElementId { get; } = $"monaco-diff-{Guid.NewGuid():N}";
    private bool _initialized;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Js.InvokeVoidAsync("monacoInterop.initDiffEditor", ElementId, OriginalValue, ModifiedValue, IsDark);
            _initialized = true;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_initialized)
        {
            await Js.InvokeVoidAsync("monacoInterop.updateDiffEditor", ElementId, OriginalValue, ModifiedValue);
        }
    }

    // SITE-FUNC-MONACO-DISPOSE-001: idempotent, and the JS side releases an editor that is already
    // gone as a no-op; a JS runtime that is itself gone has no editor left to release either.
    public async ValueTask DisposeAsync()
    {
        if (!_initialized) return;
        _initialized = false;
        try { await Js.InvokeVoidAsync("monacoInterop.disposeDiffEditor", ElementId); }
        catch (JSDisconnectedException) { /* the JS runtime is gone, and the editor with it: nothing left to release */ }
    }
}
