// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class MonacoEditor : IAsyncDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    [Parameter] public string Value { get; set; } = string.Empty;
    [Parameter] public EventCallback<string> ValueChanged { get; set; }
    [Parameter] public EventCallback<string> DebouncedValueChanged { get; set; }
    [Parameter] public int DebounceDelay { get; set; } = 500;
    [Parameter] public EventCallback OnBlur { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public bool IsDark { get; set; } = true;
    /// <summary>Raised when the user activates a <c>pipeline: &lt;name&gt;</c> reference (Ctrl/Cmd-click) in the
    /// editor - the host opens that pipeline's definition in a read-only peek pane.</summary>
    [Parameter] public EventCallback<string> OnPipelineRefClicked { get; set; }

    private string ElementId { get; } = $"monaco-{Guid.NewGuid():N}";
    private DotNetObjectReference<MonacoEditor>? _dotNetRef;
    private bool _initialized;
    private string? _pendingValue;
    private readonly CancellationTokenSource _lifetimeCts = new();
    // Read the token once while its source is alive; see OnYamlChanged.
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _debounceCts;
    private Task? _debounceTask;
    private int _debounceGeneration;
    private bool _disposed;
    private int _cursorLine = 1;
    private int _cursorColumn = 1;

    public MonacoEditor() => _lifetime = _lifetimeCts.Token;

    // A host call that resumes after the page was left (a YAML validation awaiting the API) finds the
    // frame already removed with the DOM: the editor is no longer there to update.
    private bool IsLive => _initialized && !_disposed;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("monacoInterop.init", ElementId, Value, _dotNetRef, IsDark);
            _initialized = true;

            if (_pendingValue is not null)
            {
                await Js.InvokeVoidAsync("monacoInterop.setValue", ElementId, _pendingValue);
                _pendingValue = null;
            }
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_initialized)
        {
            _pendingValue = Value;
            return;
        }

        var currentValue = await Js.InvokeAsync<string>("monacoInterop.getValue", ElementId);
        if (currentValue != Value)
        {
            await Js.InvokeVoidAsync("monacoInterop.setValue", ElementId, Value);
        }
    }

    public async Task SetValidationErrorsAsync(List<MonacoValidationError> errors)
    {
        if (IsLive)
        {
            await Js.InvokeVoidAsync("monacoInterop.setValidationErrors", ElementId, errors);
        }
    }

    public async Task ClearValidationErrorsAsync()
    {
        if (IsLive)
        {
            await Js.InvokeVoidAsync("monacoInterop.clearValidationErrors", ElementId);
        }
    }

    public async Task UpdateSuggestionsAsync(MonacoSuggestions suggestions)
    {
        if (IsLive)
        {
            await Js.InvokeVoidAsync("monacoInterop.updateSuggestions", suggestions);
        }
    }

    public async Task FormatDocumentAsync()
    {
        if (IsLive)
        {
            await Js.InvokeVoidAsync("monacoInterop.formatDocument", ElementId);
        }
    }

    [JSInvokable]
    public async Task OnYamlChanged(string value)
    {
        await ValueChanged.InvokeAsync(value);

        // Ignore editor callbacks during disposal because JavaScript may still emit changes after
        // the cancellation sources have been disposed.
        if (DebouncedValueChanged.HasDelegate && !_disposed)
        {
            _debounceCts?.Cancel();
            if (_debounceTask is not null) await _debounceTask;
            if (_disposed) return;
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            var generation = ++_debounceGeneration;
            _debounceTask = InvokeDebouncedValueChangedAsync(value, generation, _debounceCts.Token);
        }
    }

    private async Task InvokeDebouncedValueChangedAsync(
        string value, int generation, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(DebounceDelay), Clock, ct);
            if (_disposed || generation != _debounceGeneration) return;
            await InvokeAsync(() => DebouncedValueChanged.InvokeAsync(value));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    [JSInvokable]
    public async Task OnEditorBlur()
    {
        await OnBlur.InvokeAsync();
    }

    [JSInvokable]
    public async Task OnEditorSave()
    {
        await OnSave.InvokeAsync();
    }

    [JSInvokable]
    public async Task OnPipelineLinkActivated(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            await OnPipelineRefClicked.InvokeAsync(name);
    }

    [JSInvokable]
    public Task OnCursorPositionChanged(int line, int column)
    {
        _cursorLine = line;
        _cursorColumn = column;
        return InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceGeneration++;
        _lifetimeCts.Cancel();
        _debounceCts?.Cancel();
        if (_debounceTask is not null) await _debounceTask;
        _debounceCts?.Dispose();
        _lifetimeCts.Dispose();
        if (_initialized)
        {
            // SITE-FUNC-MONACO-DISPOSE-001: the JS side releases an editor that is already gone as a
            // no-op; a JS runtime that is itself gone has no editor left to release either.
            _initialized = false;
            try { await Js.InvokeVoidAsync("monacoInterop.dispose", ElementId); }
            catch (JSDisconnectedException) { /* the JS runtime is gone, and the editor with it: nothing left to release */ }
        }
        _dotNetRef?.Dispose();
    }
}

public sealed record MonacoValidationError
{
    public string Message { get; init; } = string.Empty;
    public int StartLine { get; init; } = 1;
    public int StartColumn { get; init; } = 1;
    public int EndLine { get; init; } = 1;
    public int EndColumn { get; init; } = 1000;
}

public sealed record MonacoSuggestions
{
    public List<string> LibraryNames { get; init; } = [];
    public List<string> VaultNames { get; init; } = [];
    public List<string> ServerNames { get; init; } = [];
    public List<string> VariableKeys { get; init; } = [];
}
