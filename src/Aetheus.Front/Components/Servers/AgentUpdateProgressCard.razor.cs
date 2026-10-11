// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

/// <summary>
/// Self-update progress indicator. Lives next to <c>ServerDetail</c>, listens to the existing
/// server hub (shared connection - no double-subscribe), and renders a determinate progress
/// bar that walks Queued → PickedUp → … → Done. Stays invisible at rest.
/// </summary>
public partial class AgentUpdateProgressCard : IAsyncDisposable
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter, EditorRequired] public HubConnection? Hub { get; set; }
    /// <summary>Retained for source compatibility; heartbeats alone never confirm an update.</summary>
    [Parameter] public string? CurrentAgentVersion { get; set; }
    [Parameter] public AgentUpdateRequestSummaryDto? Request { get; set; }
    /// <summary>Invoked from the Retry button shown in the Failed state (S-DES-17). Wired by the
    /// parent to its UpdateAgent action so a failed self-update can be relaunched in place.</summary>
    [Parameter] public EventCallback OnRetry { get; set; }
    /// <summary>Recette R1-42: invoked when no news of the update arrived in time, so the parent re-reads
    /// the persisted request instead of the card concluding a failure the server never stated.</summary>
    [Parameter] public EventCallback OnStale { get; set; }

    /// <summary>The backend's confirmation window after handoff (<c>AgentUpdateRepository</c>), plus a
    /// margin for its coordinator to record the verdict.</summary>
    internal static readonly TimeSpan HandoffWindow = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15);

    /// <summary>How often the card asks again while the update stays silent.</summary>
    internal const int RecheckSeconds = 60;

    private AgentUpdatePhase? _phase;
    private int _percent;
    private string? _message;
    private DateTime _showDoneUntil = DateTime.MinValue;
    private bool _fadingOut;
    private int? _requestId;
    private AgentUpdateRequestStatus? _requestStatus;
    private bool _checking;

    // Registered handlers - kept so we can detach them in DisposeAsync to avoid leaks if the
    // hub connection survives this component (it always does - the connection is owned by
    // ServerDetail).
    private IDisposable? _onQueued;
    private IDisposable? _onProgress;
    private IDisposable? _onOffline;
    private IDisposable? _onConfirmed;
    private IDisposable? _onFailed;
    private System.Threading.Timer? _staleTimer;

    protected override void OnParametersSet()
    {
        ApplyPersistedRequest();
        if (Hub is null) return;
        Subscribe(Hub);
        // OnParametersSet can fire on each render; guard against double-subscribe by storing
        // the disposables and detaching the previous batch when re-subscribing on a fresh hub.
        // In practice the parent passes the same instance throughout the page's lifetime, so
        // this is belt-and-braces.
    }

    private void Subscribe(HubConnection hub)
    {
        _onQueued ??= hub.On<int>("AgentUpdateQueued", serverId =>
        {
            if (serverId != ServerId) return;
            _phase = AgentUpdatePhase.Queued;
            _percent = 0;
            _message = null;
            _checking = false;
            StartStaleTimer(120);
            _ = InvokeAsync(StateHasChanged);
        });

        _onProgress ??= hub.On<AgentUpdateProgressDto>("AgentUpdateProgress", dto =>
        {
            if (dto.ServerId != ServerId) return;
            // Monotonic guard: ignore phase regressions from network reordering or a retry beat.
            // Failed is special - always shown.
            if (_phase is { } current && dto.Phase != AgentUpdatePhase.Failed && dto.Phase < current)
                return;

            _phase = dto.Phase;
            _percent = dto.Percent;
            _message = dto.Message;
            _checking = false;
            if (dto.Phase is AgentUpdatePhase.Failed or AgentUpdatePhase.Done)
                CancelStaleTimer();
            else
                StartStaleTimer(StaleSecondsFor(dto.Phase));
            _ = InvokeAsync(StateHasChanged);
        });

        _onOffline ??= hub.On<int>("ServerOffline", serverId =>
        {
            if (serverId != ServerId) return;
            // ServerOffline is only meaningful here AFTER we've seen LaunchingUpdater - that
            // tells us the offline is the planned binary swap, not a real crash.
            if (_phase is not (AgentUpdatePhase.LaunchingUpdater
                or AgentUpdatePhase.AgentOffline)) return;
            _phase = AgentUpdatePhase.AgentOffline;
            _percent = 95;
            _message = L["AgentUpdateRestartingHint"];
            _ = InvokeAsync(StateHasChanged);
        });

        _onConfirmed ??= hub.On<int, int>("AgentUpdateConfirmed", (serverId, requestId) =>
        {
            if (serverId != ServerId) return;
            HandleConfirmed(requestId, Request?.TargetVersion);
        });

        _onFailed ??= hub.On<int, int>("AgentUpdateFailed", (serverId, requestId) =>
        {
            if (serverId != ServerId) return;
            _requestId = requestId;
            CancelStaleTimer();
            _checking = false;
            _phase = AgentUpdatePhase.Failed;
            _percent = 0;
            _message = Request?.FailureDiagnostic ?? L["AgentUpdateFailed"];
            _ = InvokeAsync(StateHasChanged);
        });
    }

    public void HandleConfirmed(int requestId, string? targetVersion)
    {
        _requestId = requestId;
        CancelStaleTimer();
        _checking = false;
        _phase = AgentUpdatePhase.Done;
        _percent = 100;
        _message = !string.IsNullOrEmpty(targetVersion)
            ? string.Format(L["AgentUpdateDoneHint"], targetVersion)
            : null;
        if (!string.IsNullOrEmpty(targetVersion))
            Toast.Success("AgentUpdated", "AgentUpdateDoneHint", targetVersion);
        _showDoneUntil = DateTime.Now.AddSeconds(5);
        _ = InvokeAsync(async () =>
        {
            StateHasChanged();
            // S-DES-16: fade the card out over the last 0.4s instead of an abrupt removal.
            await Task.Delay(TimeSpan.FromSeconds(4.6));
            _fadingOut = true;
            StateHasChanged();
            await Task.Delay(TimeSpan.FromSeconds(0.4));
            _phase = null;
            _fadingOut = false;
            StateHasChanged();
        });
    }

    /// <summary>
    /// Compatibility seam for older parents. A heartbeat alone is deliberately insufficient:
    /// confirmation arrives through <c>AgentUpdateConfirmed</c> after backend validation.
    /// </summary>
    public void HandleHeartbeat(string newAgentVersion)
    {
    }

    /// <summary>
    /// Applies the persisted request. A new request is shown as it stands. For the request already
    /// followed, recette R1-42: a status change read again (after a reconnect or a silent stretch) is
    /// applied too, because a hub event lost while the tab slept is never replayed; a persisted
    /// confirmation ends the card on Done whatever it showed, a persisted failure on Failed, and an
    /// in-progress status only moves the card forward.
    /// </summary>
    private void ApplyPersistedRequest()
    {
        if (Request is null) return;
        var followed = Request.RequestId == _requestId;
        if (followed && Request.Status == _requestStatus) return;
        _requestId = Request.RequestId;
        _requestStatus = Request.Status;
        var (phase, percent) = MapStatus(Request.Status);

        if (!followed)
        {
            _phase = phase;
            _percent = percent;
            _message = PersistedMessage(Request);
            _checking = false;
            return;
        }

        switch (Request.Status)
        {
            case AgentUpdateRequestStatus.Confirmed:
                if (_phase != AgentUpdatePhase.Done)
                    HandleConfirmed(Request.RequestId, Request.TargetVersion);
                return;
            case AgentUpdateRequestStatus.Failed:
                CancelStaleTimer();
                _checking = false;
                _phase = AgentUpdatePhase.Failed;
                _percent = 0;
                _message = Request.FailureDiagnostic ?? L["AgentUpdateFailed"];
                return;
            default:
                if (phase is null || _phase is AgentUpdatePhase.Done or AgentUpdatePhase.Failed
                    || (_phase is { } current && phase <= current))
                    return;
                _phase = phase;
                _percent = percent;
                _message = PersistedMessage(Request);
                _checking = false;
                return;
        }
    }

    private static (AgentUpdatePhase? Phase, int Percent) MapStatus(AgentUpdateRequestStatus status) => status switch
    {
        AgentUpdateRequestStatus.WaitingForIdle => (AgentUpdatePhase.Queued, 0),
        AgentUpdateRequestStatus.Queued => (AgentUpdatePhase.Queued, 0),
        AgentUpdateRequestStatus.Downloading => (AgentUpdatePhase.Downloading, 20),
        AgentUpdateRequestStatus.Staging => (AgentUpdatePhase.Extracting, 60),
        AgentUpdateRequestStatus.Handoff => (AgentUpdatePhase.AgentOffline, 95),
        AgentUpdateRequestStatus.Confirmed => (AgentUpdatePhase.Done, 100),
        AgentUpdateRequestStatus.Failed => (AgentUpdatePhase.Failed, 0),
        _ => (null, 0)
    };

    private string? PersistedMessage(AgentUpdateRequestSummaryDto request) =>
        request.Status == AgentUpdateRequestStatus.WaitingForIdle
            ? string.Format(L["AgentUpdateWaitingForTasks"], request.BlockingTaskCount)
            : request.FailureDiagnostic;

    // S-DES-17: relaunch a failed self-update. Clear the card first so the parent's trigger drives
    // a fresh Queued→… cycle through the existing hub events.
    private async Task RetryAsync()
    {
        CancelStaleTimer();
        _checking = false;
        _phase = null;
        _percent = 0;
        _message = null;
        StateHasChanged();
        await OnRetry.InvokeAsync();
    }

    private string? FailureLogsPath => _requestId is { } requestId
        ? $"/admin/system-logs?search={Uri.EscapeDataString(LogCorrelationIds.AgentUpdate(requestId))}"
        : null;

    private string PhaseLabel(AgentUpdatePhase? phase) => phase switch
    {
        AgentUpdatePhase.Queued => "AgentUpdatePhase_Queued",
        AgentUpdatePhase.PickedUp => "AgentUpdatePhase_PickedUp",
        AgentUpdatePhase.Downloading => "AgentUpdatePhase_Downloading",
        AgentUpdatePhase.Downloaded => "AgentUpdatePhase_Downloaded",
        AgentUpdatePhase.Extracting => "AgentUpdatePhase_Extracting",
        AgentUpdatePhase.LaunchingUpdater => "AgentUpdatePhase_LaunchingUpdater",
        AgentUpdatePhase.AgentOffline => "AgentUpdatePhase_AgentOffline",
        AgentUpdatePhase.Done => "AgentUpdatePhase_Done",
        AgentUpdatePhase.Failed => "AgentUpdatePhase_Failed",
        _ => "AgentUpdatePhase_Queued"
    };

    private static string PhaseIcon(AgentUpdatePhase? phase) => phase switch
    {
        AgentUpdatePhase.Queued => "schedule",
        AgentUpdatePhase.PickedUp => "play_arrow",
        AgentUpdatePhase.Downloading => "cloud_download",
        AgentUpdatePhase.Downloaded => "task",
        AgentUpdatePhase.Extracting => "folder_zip",
        AgentUpdatePhase.LaunchingUpdater => "rocket_launch",
        AgentUpdatePhase.AgentOffline => "hourglass_top",
        AgentUpdatePhase.Done => "check_circle",
        AgentUpdatePhase.Failed => "error",
        _ => "schedule"
    };

    private static string PhaseIconClass(AgentUpdatePhase? phase) => phase switch
    {
        AgentUpdatePhase.Done => "agent-update-phase-done",
        AgentUpdatePhase.Failed => "agent-update-phase-failed",
        _ => "agent-update-phase-active"
    };

    private static OmniTone PhaseBadge(AgentUpdatePhase? phase) => phase switch
    {
        AgentUpdatePhase.Done => OmniTone.Success,
        AgentUpdatePhase.Failed => OmniTone.Danger,
        AgentUpdatePhase.AgentOffline => OmniTone.Warning,
        _ => OmniTone.Accent
    };

    /// <summary>
    /// How long the card waits for news before it re-reads the request. Once the updater is launched
    /// the agent is expected to be silent while it restarts, so the wait follows the backend's
    /// confirmation window from the handoff instead of a fixed 90 s.
    /// </summary>
    internal int StaleSecondsFor(AgentUpdatePhase phase)
    {
        if (phase is not (AgentUpdatePhase.LaunchingUpdater or AgentUpdatePhase.AgentOffline))
            return 90;
        var deadline = Request is { HandoffAt: { } handoffAt } request && request.RequestId == _requestId
            ? handoffAt + HandoffWindow
            : DateTime.Now + HandoffWindow;
        return Math.Max(5, (int)Math.Ceiling((deadline - DateTime.Now).TotalSeconds));
    }

    /// <summary>
    /// Recette R1-42: silence is not a failure. The verdict belongs to the server, which fails an
    /// expired request itself and says so (<c>AgentUpdateFailed</c>); a confirmation sent while the tab
    /// slept is simply lost. So the card shows that it is checking, asks its parent to re-read the
    /// persisted request, and asks again later while nothing settles.
    /// </summary>
    internal void OnStaleTimerElapsed()
    {
        if (_phase is AgentUpdatePhase.Done or AgentUpdatePhase.Failed or null)
            return;
        _checking = true;
        _message = L["AgentUpdateCheckingHint"];
        StartStaleTimer(RecheckSeconds);
        _ = InvokeAsync(async () =>
        {
            StateHasChanged();
            await OnStale.InvokeAsync();
        });
    }

    private void StartStaleTimer(int seconds)
    {
        _staleTimer?.Dispose();
        _staleTimer = new System.Threading.Timer(
            _ => OnStaleTimerElapsed(), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    private void CancelStaleTimer()
    {
        _staleTimer?.Dispose();
        _staleTimer = null;
    }

    public ValueTask DisposeAsync()
    {
        CancelStaleTimer();
        _onQueued?.Dispose();
        _onProgress?.Dispose();
        _onOffline?.Dispose();
        _onConfirmed?.Dispose();
        _onFailed?.Dispose();
        return ValueTask.CompletedTask;
    }
}
