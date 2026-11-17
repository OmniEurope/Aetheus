// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Whether the log terminal stays pinned to the newest line, and the scrolling that follows from it.
/// A live test run emits thousands of lines, so the page re-renders constantly (the one-second
/// duration tick alone): the last streamed line count is kept here so a re-render with no new line
/// does not re-pin and fight a user reading history.
///
/// Extracted from <see cref="PipelineRun"/> when it crossed the file-size budget (FileSizeAuditTests).
/// The element reference stays on the component, which is where the markup binds it.
/// </summary>
internal sealed class PipelineRunLogFollow(IJSRuntime js, Func<Task> changed) : IDisposable
{
    public bool Following { get; private set; } = true;

    private int _lastAutoScrollCount = -1;
    private string? _watchedTerminalId;
    private DotNetObjectReference<PipelineRunLogFollow>? _self;

    /// <summary>Forget the last pinned position, so the next render re-pins even at the same count.</summary>
    public void Rearm() => _lastAutoScrollCount = -1;

    /// <summary>
    /// The reader scrolled the terminal by hand (touch, wheel, scrollbar): away from the tail, following
    /// stops, or every new line of a live run would drag them back down; back at the tail, it resumes.
    /// </summary>
    [JSInvokable]
    public async Task OnUserScrolled(bool atBottom)
    {
        if (atBottom == Following) return;
        Following = atBottom;
        if (atBottom) Rearm();
        await changed();
    }

    /// <summary>Called on every render: pins to the bottom only when following and new lines arrived.</summary>
    public async Task AfterRenderAsync(ElementReference terminal, int lineCount)
    {
        await WatchAsync(terminal);
        // R-21: a terminal that is not mounted yet (a fresh load fills the log before the pane is drawn) cannot be
        // scrolled; the count is then not consumed, or the log would stay at its top with the tail virtualized away.
        if (terminal.Id is null) return;
        if (!Following || lineCount == _lastAutoScrollCount) return;
        _lastAutoScrollCount = lineCount;
        await ScrollAsync("Aetheus.scrollToBottom", terminal);
    }

    /// <summary>R-21: the list just reached its full height; while following, pin to the tail again.</summary>
    public async Task RepinAsync(ElementReference terminal)
    {
        if (!Following || terminal.Id is null) return;
        await ScrollAsync("Aetheus.scrollToBottom", terminal);
    }

    /// <summary>Jumping to the top means the user wants to read history, not follow the tail.</summary>
    public async Task ToTopAsync(ElementReference terminal)
    {
        Following = false;
        await ScrollAsync("Aetheus.scrollToTop", terminal);
    }

    public async Task ToBottomAsync(ElementReference terminal)
    {
        Following = true;
        Rearm();
        await ScrollAsync("Aetheus.scrollToBottom", terminal);
    }

    public async Task ToggleAsync(ElementReference terminal)
    {
        Following = !Following;
        if (!Following) return;
        Rearm();
        await ScrollAsync("Aetheus.scrollToBottom", terminal);
    }

    /// <summary>Start following again (a newly selected step opens at its tail).</summary>
    public void Follow()
    {
        Following = true;
        Rearm();
    }

    /// <summary>Hooks the reader's own scrolling once per terminal element (a collapsed pane mounts another).</summary>
    private async Task WatchAsync(ElementReference terminal)
    {
        if (terminal.Id is null || terminal.Id == _watchedTerminalId) return;
        _self ??= DotNetObjectReference.Create(this);
        try
        {
            await js.InvokeVoidAsync("Aetheus.watchLogFollow", terminal, _self);
            _watchedTerminalId = terminal.Id;
        }
        catch (JSException)
        {
            // The terminal unmounted before the hook ran (pane collapsed); the next render retries.
        }
    }

    public void Dispose() => _self?.Dispose();

    private async Task ScrollAsync(string function, ElementReference terminal)
    {
        // The terminal is not mounted when the logs pane is collapsed; nothing to scroll, not an error.
        try { await js.InvokeVoidAsync(function, terminal); }
        catch (JSException) { }
    }
}
