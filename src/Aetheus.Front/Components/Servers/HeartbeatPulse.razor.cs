// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers;

/// <summary>
/// Heartbeat pulse ring - a thick-bordered circle whose stroke fills over the 30 s
/// heartbeat cadence and resets each time a fresh beat lands. While the server is alive
/// the centre counts the cadence down (30 → 0, then restarts) so it reads as a live timer
/// to the next expected beat; offline/disabled it falls back to showing how stale the last
/// signal is. Paired with the live state label (alive / offline / disabled).
/// </summary>
public partial class HeartbeatPulse : IDisposable
{
    private const double Radius = 42d;
    private static readonly double Circumference = 2 * Math.PI * Radius;

    // HBSY: single source of truth for the beat cadence (seconds). Two invariants ride on this value
    // and a drift would silently desync the ring from the real beat:
    //   1. It mirrors the `--heartbeat-cadence` CSS variable in app.css (the CSS loop runs the visual
    //      fill; this constant drives the negative-delay phase-sync math). HeartbeatCadenceContractTests
    //      fails the build if the two drift apart.
    //   2. It equals the agent default `AetheusAgentOptions.HeartbeatIntervalSeconds` (30 s). The
    //      cadence is contractual at that default; the live per-server interval is not reported yet, so
    //      if an operator retunes the agent the constant (and the CSS var) must move with it.
    private const int CadenceSeconds = 30;

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;

    private ElementReference _progressRef;
    private long _lastPhasedBeatKey = long.MinValue;

    /// <summary>The browser-local refresh timestamp displayed inside the server tile.</summary>
    [Parameter] public DateTime? LastUpdated { get; set; }

    private string _dashArray = "";
    private string _dashOffset = "";
    private long _beatKey;
    private string _centerText = "";
    private string _stateLabel = "";
    private string _stateClass = "";
    private string _tooltip = "";
    private string _ariaLabel = "";
    private DateTime? _lastBeatLocal;
    private CancellationTokenSource? _ticker;

    protected override void OnInitialized()
    {
        Recompute();
        _ticker = new CancellationTokenSource();
        _ = TickAsync(_ticker.Token);
    }

    protected override void OnParametersSet() => Recompute();

    // Phase-align the freshly (re)created ring with the real beat age. The progress circle is
    // @key'd on the beat, so it is recreated only when a new beat lands (elapsed ≈ 0 → no shift)
    // or on first render after page load (elapsed = true age → shift the loop into phase). Guarded
    // by _lastPhasedBeatKey so the per-second ticker re-renders don't re-push the delay.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Server.Status != ServerStatus.Online || _lastBeatLocal is not { } lb) return;
        if (_beatKey == _lastPhasedBeatKey) return;
        _lastPhasedBeatKey = _beatKey;
        var elapsed = DateTime.Now - lb;
        var secondsIntoCadence = elapsed.TotalSeconds % CadenceSeconds;
        if (secondsIntoCadence < 0) secondsIntoCadence = 0;
        try
        {
            await JS.InvokeVoidAsync("Aetheus.syncHeartbeatPhase", _progressRef,
                secondsIntoCadence.ToString("0.###", CultureInfo.InvariantCulture));
        }
        catch (JSException) { /* element gone / prerender - phase-sync is best-effort cosmetic */ }
    }

    // Drive the centre counter and ring fill once per second; the ring also resets
    // automatically whenever the parent feeds a newer LastHeartbeat (SignalR-fed).
    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                Recompute();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }

    private void Recompute()
    {
        // LastHeartbeat arrives browser-local (global UTC→Local JSON converter); never
        // compare to default(DateTime) - a "never reported" beat reads as Year < 2000.
        var last = Server.LastHeartbeat;
        _lastBeatLocal = last.Year < 2000 ? null : last;

        var elapsed = _lastBeatLocal is { } lb ? DateTime.Now - lb : TimeSpan.Zero;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        // Ring + countdown share one anchor so they can't drift apart: the circle is re-keyed
        // both on a fresh beat (new Ticks) AND on every 30 s cadence rollover while alive (the
        // cycle index below). Re-keying recreates the element, restarting the CSS fill from empty
        // at the exact tick the countdown wraps back to 30, so the glyph and the ring stay in
        // lockstep instead of slowly desyncing against the browser's free-running animation clock.
        // Code only sets the static base offset the animation overrides while alive: empty for a
        // live ring, full for an offline/disabled (static) one.
        var online = Server.Status == ServerStatus.Online;
        var cycle = online ? (long)(elapsed.TotalSeconds / CadenceSeconds) : 0;
        _dashArray = Circumference.ToString("0.##", CultureInfo.InvariantCulture);
        _dashOffset = online ? _dashArray : "0";
        _beatKey = unchecked((_lastBeatLocal?.Ticks ?? 0) + cycle);

        _stateClass = Server.Status switch
        {
            ServerStatus.Online => "heartbeat-pulse-online",
            ServerStatus.Offline => "heartbeat-pulse-offline",
            _ => "heartbeat-pulse-disabled"
        };
        _stateLabel = Server.Status switch
        {
            ServerStatus.Online => L["ServerAlive"],
            ServerStatus.Offline => L["Offline"],
            _ => L["Disabled"]
        };
        _centerText = ComputeCenterText(online, elapsed);
        _tooltip = _lastBeatLocal is { } lbt ? lbt.ToString("F") : L["Never"];
        _ariaLabel = $"{_stateLabel} · {_centerText}";
    }

    // Alive: a live countdown to the next expected beat, synced with the 30 s ring (30 → 0,
    // wrapping back to 30 each cadence). Offline/disabled: the ring is static, so show how
    // long ago the last beat was instead. No beat ever reported → em dash.
    private string ComputeCenterText(bool online, TimeSpan elapsed)
    {
        if (_lastBeatLocal is null) return "-";
        if (!online) return FormatCompact(elapsed);

        var inCycle = elapsed.TotalSeconds % CadenceSeconds;
        var remaining = (int)Math.Round(CadenceSeconds - inCycle);
        remaining = Math.Clamp(remaining, 0, CadenceSeconds);
        return string.Format(L["DurationSecondsShort"], remaining);
    }

    private string FormatCompact(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return string.Format(L["DurationSecondsShort"], (int)span.TotalSeconds);
        if (span.TotalMinutes < 60) return string.Format(L["DurationMinutesShort"], (int)span.TotalMinutes);
        if (span.TotalHours < 24) return string.Format(L["DurationHoursShort"], (int)span.TotalHours);
        return string.Format(L["DurationDaysShort"], (int)span.TotalDays);
    }

    public void Dispose()
    {
        _ticker?.Cancel();
        _ticker?.Dispose();
    }
}
