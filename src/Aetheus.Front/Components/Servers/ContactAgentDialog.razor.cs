// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

/// <summary>
/// Probes a server's agent with a bounded timeout, up to <see cref="MaxAttempts"/>
/// times. An indeterminate progress bar acts as the in-flight indicator; on
/// success the result is shown until the user dismisses the dialog with the Close
/// button. On final failure the structured error returned by the backend is shown
/// inline.
/// </summary>
public partial class ContactAgentDialog : IAsyncDisposable
{
    // Once Type=exec is in place on the VPS, the agent sends a steady 30 s
    // heartbeat - attempt 1 succeeds ~always, and 2 is a real margin for a flap.
    private const int MaxAttempts = 2;

    /// <summary>Window each probe attempt waits before falling through to the next.</summary>
    private const int AttemptWindowSeconds = 6;

    [Parameter] public int ServerId { get; set; }
    [Parameter] public string ServerName { get; set; } = string.Empty;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    private enum ContactState { Running, Success, Failed }

    private ContactState _state = ContactState.Running;
    private int _attempt;
    private string _errorMessage = string.Empty;
    private ContactAgentResultDto? _result;
    private CancellationTokenSource? _cts;
    private HubConnection? _hub;

    // Lazy "Why offline?" diagnostic - only fetched on demand from the Failed
    // state. Kept here (vs. an inline cascading load) because the user often
    // dismisses on the error message alone; no need to spend a roundtrip then.
    private ServerDiagnosticDto? _diagnostic;
    private bool _diagnosticLoading;

    protected override async Task OnInitializedAsync()
    {
        await StartHeartbeatListenerAsync();
        await StartAsync();
    }

    /// <summary>
    /// S-FEAT-08: subscribe to the server's SignalR group so a heartbeat arriving
    /// mid-probe confirms reachability instantly - a push channel that complements
    /// the bounded polling loop (whichever fires first wins).
    /// </summary>
    private async Task StartHeartbeatListenerAsync()
    {
        try
        {
            _hub = HubFactory.Create("servers");
            _hub.On<ServerHeartbeatDto>("Heartbeat", _ =>
            {
                if (_state != ContactState.Running) return Task.CompletedTask;
                return InvokeAsync(async () =>
                {
                    await CancelRunningAsync();
                    await EnterSuccessAsync(new ContactAgentResultDto
                    {
                        Reachable = true,
                        LastHeartbeat = DateTime.Now,
                        SecondsSinceLastHeartbeat = 0
                    });
                });
            });
            // Group membership is per-connection and lost on auto-reconnect - re-join (polling backs this up).
            _hub.RejoinOnReconnect(() => _hub.InvokeAsync("JoinServerGroup", ServerId));
            await _hub.StartAsync();
            await _hub.InvokeAsync("JoinServerGroup", ServerId);
        }
        catch
        {
            // Push is best-effort - the polling loop still provides the answer.
        }
    }

    private async Task StartAsync()
    {
        await CancelRunningAsync();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _state = ContactState.Running;
        _attempt = 0;
        _errorMessage = string.Empty;
        _result = null;

        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                _attempt = attempt;
                await InvokeAsync(StateHasChanged);

                var result = await ProbeOnceAsync(token).ConfigureAwait(false);
                if (result is { Reachable: true })
                {
                    await EnterSuccessAsync(result);
                    return;
                }

                _result = result;
                _errorMessage = ResolveError(result);

                // Last attempt failed - surface the error and stop.
                if (attempt == MaxAttempts)
                {
                    _state = ContactState.Failed;
                    await InvokeAsync(StateHasChanged);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dialog closed or a new run started - nothing to surface.
        }
    }

    /// <summary>
    /// One attempt: the probe races the countdown. A reachable answer is returned at once, there is
    /// no reason to sit on the countdown after the answer has come in; a not-yet-reachable answer
    /// still drains the countdown to keep the rhythm between attempts.
    /// </summary>
    private async Task<ContactAgentResultDto?> ProbeOnceAsync(CancellationToken token)
    {
        var probe = Api.Servers.ContactAgentAsync(ServerId, token);
        var countdown = RunCountdownAsync(token);
        var first = await Task.WhenAny(probe, countdown).ConfigureAwait(false);
        var result = await probe.ConfigureAwait(false);
        if (first == probe && result is not { Reachable: true })
            await countdown.ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Bounds an attempt to <see cref="AttemptWindowSeconds"/>. The actual probe
    /// runs concurrently; this provides the visible per-attempt timeout window.
    /// </summary>
    private static async Task RunCountdownAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(AttemptWindowSeconds), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by an early success or dialog close - nothing to surface.
        }
    }

    /// <summary>Transitions to Success - the user dismisses the dialog manually.</summary>
    private async Task EnterSuccessAsync(ContactAgentResultDto result)
    {
        _result = result;
        _state = ContactState.Success;
        await InvokeAsync(StateHasChanged);
    }

    private string ResolveError(ContactAgentResultDto? result)
    {
        if (result is null) return L["ContactAgentRequestFailed"];
        return string.IsNullOrWhiteSpace(result.Error) ? L["ContactAgentUnreachable"] : result.Error;
    }

    private string FormatSeconds(double seconds)
    {
        if (seconds < 60) return string.Format(L["ContactAgentSecondsAgo"], Math.Round(seconds));
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Format(L["ContactAgentHoursAgo"], (int)span.TotalHours, span.Minutes)
            : string.Format(L["ContactAgentMinutesAgo"], span.Minutes, span.Seconds);
    }

    /// <summary>
    /// Loads the diagnostic on demand from the Failed state. Idempotent - a
    /// second click while loading is ignored; a click after a successful load
    /// re-fetches (the operator may want a fresh read after a fix attempt).
    /// </summary>
    private async Task LoadDiagnosticAsync()
    {
        if (_diagnosticLoading) return;
        _diagnosticLoading = true;
        await InvokeAsync(StateHasChanged);
        _diagnostic = await Api.Servers.GetServerDiagnosticAsync(ServerId);
        _diagnosticLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>S-DES-04: semantic colour for the token staleness mini-bar.</summary>
    private static OmniTone TokenBarVariant(double daysRemaining) => daysRemaining switch
    {
        < 7 => OmniTone.Danger,
        < 30 => OmniTone.Warning,
        _ => OmniTone.Success
    };

    /// <summary>S-DES-04: fill width clamped to 0-100 based on a 365-day scale.</summary>
    private static int TokenBarPercent(double daysRemaining)
    {
        if (daysRemaining <= 0) return 0;
        return (int)Math.Clamp(daysRemaining / 365 * 100, 2, 100);
    }

    /// <summary>Aborts the in-flight probe and closes the dialog (unsuccessful).</summary>
    private async Task CancelAndCloseAsync()
    {
        await CancelRunningAsync();
        Dialog.Close(false);
    }

    private async Task CancelRunningAsync()
    {
        if (_cts is null) return;
        try { await _cts.CancelAsync(); }
        catch (ObjectDisposedException) { /* already disposed */ }
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CancelRunningAsync();
        if (_hub is not null)
        {
            try { await _hub.InvokeAsync("LeaveServerGroup", ServerId); } catch { /* best-effort */ }
            try { await _hub.DisposeAsync(); } catch { /* best-effort */ }
            _hub = null;
        }
    }
}
