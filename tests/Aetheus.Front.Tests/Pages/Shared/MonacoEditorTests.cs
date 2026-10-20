// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class MonacoEditorTests : BunitContext
{
    private readonly ObservedFakeClock _clock = new(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));

    public MonacoEditorTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<TimeProvider>(_clock);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Renders_EditorDiv()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "test: value")
            .Add(x => x.IsDark, true));

        Assert.Contains("monaco-", cut.Markup);
    }

    [Fact]
    public void CallsJsInit_OnFirstRender()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "hello: world"));

        var invocations = JSInterop.Invocations;
        Assert.Contains(invocations, i => i.Identifier == "monacoInterop.init");
    }

    [Fact]
    public void DarkMode_DefaultTrue()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "yaml: content"));

        var init = JSInterop.Invocations
            .FirstOrDefault(i => i.Identifier == "monacoInterop.init");
        Assert.Equal(true, init.Arguments[3]);
    }

    [Fact]
    public void LightMode_PassesFalse()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "")
            .Add(x => x.IsDark, false));

        var init = JSInterop.Invocations
            .FirstOrDefault(i => i.Identifier == "monacoInterop.init");
        Assert.Equal(false, init.Arguments[3]);
    }

    [Fact]
    public async Task OnYamlChanged_InvokesCallback()
    {
        string? changed = null;
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "")
            .Add(x => x.ValueChanged, (string v) => changed = v));

        await cut.Instance.OnYamlChanged("new value");

        Assert.Equal("new value", changed);
    }

    [Fact]
    public async Task OnEditorBlur_InvokesCallback()
    {
        var blurred = false;
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "")
            .Add(x => x.OnBlur, () => { blurred = true; return Task.CompletedTask; }));

        await cut.Instance.OnEditorBlur();

        Assert.True(blurred);
    }

    [Fact]
    public async Task OnEditorSave_InvokesCallback()
    {
        var saved = false;
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "")
            .Add(x => x.OnSave, () => { saved = true; return Task.CompletedTask; }));

        await cut.Instance.OnEditorSave();

        Assert.True(saved);
    }

    [Fact]
    public async Task OnCursorPositionChanged_UpdatesState()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "line1\nline2"));

        await cut.Instance.OnCursorPositionChanged(2, 5);

        // The handler stores the reported line/column into the cursor-position state.
        var line = (int)typeof(MonacoEditor)
            .GetField("_cursorLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        var column = (int)typeof(MonacoEditor)
            .GetField("_cursorColumn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, line);
        Assert.Equal(5, column);
    }

    [Fact]
    public async Task DisposeAsync_CallsJsDispose()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "content"));

        await cut.Instance.DisposeAsync();

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "monacoInterop.dispose");
    }

    [Fact]
    public async Task DebouncedValueChanged_OnlyPublishesLatestValue()
    {
        var published = new List<string>();
        var callback = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, string.Empty)
            .Add(x => x.DebounceDelay, 500)
            .Add(x => x.DebouncedValueChanged, (string value) =>
            {
                published.Add(value);
                callback.TrySetResult(value);
            }));

        // The debounce runs on the simulated clock: "second" arrives inside the window of "first" however
        // long the machine takes between the two, and the window ends when the clock is moved.
        await cut.Instance.OnYamlChanged("first");
        await cut.Instance.OnYamlChanged("second");
        Assert.Equal(1, _clock.Pending(TimeSpan.FromMilliseconds(500)));
        Assert.Empty(published);
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        var actual = await HangGuard.AwaitAsync(callback.Task, "the debounced value");

        Assert.Equal("second", actual);
        Assert.Equal(["second"], published);
    }

    [Fact]
    public async Task DisposeAsync_CancelsPendingDebouncedCallback()
    {
        var published = new List<string>();
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, string.Empty)
            .Add(x => x.DebounceDelay, 200)
            .Add(x => x.DebouncedValueChanged, (string value) => published.Add(value)));
        await cut.Instance.OnYamlChanged("late");
        Assert.Equal(1, _clock.Pending(TimeSpan.FromMilliseconds(200)));

        await cut.Instance.DisposeAsync();

        // The pending delay was cancelled: nothing is left that moving the clock could publish.
        Assert.Equal(0, _clock.Pending(TimeSpan.FromMilliseconds(200)));
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Empty(published);
    }

    [Fact]
    public async Task SetValidationErrorsAsync_CallsJsInterop()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "content"));

        var errors = new List<MonacoValidationError>
        {
            new() { Message = "Error 1", StartLine = 1, EndLine = 1 }
        };
        await cut.Instance.SetValidationErrorsAsync(errors);

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "monacoInterop.setValidationErrors");
    }

    [Fact]
    public async Task ClearValidationErrorsAsync_CallsJsInterop()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "content"));

        await cut.Instance.ClearValidationErrorsAsync();

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "monacoInterop.clearValidationErrors");
    }

    [Fact]
    public async Task FormatDocumentAsync_CallsJsInterop()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "content"));

        await cut.Instance.FormatDocumentAsync();

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "monacoInterop.formatDocument");
    }

    [Fact]
    public async Task UpdateSuggestionsAsync_CallsJsInterop()
    {
        var cut = Render<MonacoEditor>(p => p
            .Add(x => x.Value, "content"));

        var suggestions = new MonacoSuggestions
        {
            LibraryNames = ["lib1"],
            VaultNames = ["vault1"],
            ServerNames = ["server1"],
            VariableKeys = ["key1"]
        };
        await cut.Instance.UpdateSuggestionsAsync(suggestions);

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "monacoInterop.updateSuggestions");
    }

    // SITE-FUNC-MONACO-DISPOSE-001: leaving the page after the JS runtime is gone threw out of DisposeAsync.
    [Fact]
    public async Task DisposeAsync_ToleratesADisconnectedJsRuntime()
    {
        JSInterop.SetupVoid("monacoInterop.dispose", _ => true)
            .SetException(new Microsoft.JSInterop.JSDisconnectedException("gone"));
        var cut = Render<MonacoEditor>(p => p.Add(x => x.Value, "content"));

        var error = await Record.ExceptionAsync(() => cut.Instance.DisposeAsync().AsTask());

        Assert.Null(error);
    }
}
