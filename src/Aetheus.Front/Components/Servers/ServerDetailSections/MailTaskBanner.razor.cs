// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary> The banner shown while the mail page's tasks run. It names them and counts the time
/// since the first was queued, moving once a second while it is on the page; the host removes it
/// when the last task completes. </summary>
public partial class MailTaskBanner : IDisposable
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    /// <summary>The names of the running tasks, in the order they were queued.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<string> Labels { get; set; } = [];

    /// <summary>When the first of them was queued.</summary>
    [Parameter, EditorRequired] public DateTimeOffset Since { get; set; }

    private PeriodicTimer? _ticker;

    private string Elapsed => (Clock.GetUtcNow() - Since).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    protected override void OnInitialized()
    {
        var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1), Clock);
        _ticker = ticker;
        _ = TickAsync(ticker);
    }

    private async Task TickAsync(PeriodicTimer ticker)
    {
        try
        {
            while (await ticker.WaitForNextTickAsync().ConfigureAwait(false))
                await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // The banner went away while the timer waited.
        }
    }

    public void Dispose()
    {
        _ticker?.Dispose();
        _ticker = null;
    }
}
