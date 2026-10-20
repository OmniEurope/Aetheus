// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public sealed record ServerOfflineNotificationPayload(
    int ServerId, string ServerName, DateTime? LastHeartbeat, ServerReachability Reachability = ServerReachability.NotProbed);

public sealed class AlertNotificationService : IAsyncDisposable
{
    private readonly AuthStateProvider _auth;
    private readonly HubConnectionFactory _hubFactory;
    private HubConnection? _hub;
    private int _unreadCount;
    private const int MaxRecent = 50;
    private readonly object _recentGate = new();
    private readonly List<AlertTriggeredDto> _recent = [];

    public int UnreadCount => _unreadCount;

    /// <summary>The most recent fired alerts (newest first), so clicking the alerts menu actually shows
    /// what the badge counted instead of an empty page. Client-side only - fired alerts aren't persisted
    /// server-side; this is a rolling in-session buffer.</summary>
    public IReadOnlyList<AlertTriggeredDto> Recent
    {
        get { lock (_recentGate) return _recent.ToList(); }
    }

    public event Action? OnChange;
    /// <summary>Raised when the backend broadcasts a <c>ServerOffline</c> event on the alerts hub.</summary>
    public event Action<ServerOfflineNotificationPayload>? OnServerOffline;

    public AlertNotificationService(AuthStateProvider auth, HubConnectionFactory hubFactory)
    {
        _auth = auth;
        _hubFactory = hubFactory;
    }

    public async Task StartAsync()
    {
        if (_hub is not null) return;
        if (!_auth.IsAuthenticated || string.IsNullOrEmpty(_auth.Token)) return;
        // The alerts hub is Admin-only (AlertHub is [Authorize(Roles="Admin")]). Non-admins would
        // otherwise negotiate and get a 403 the browser logs to the console before SignalR swallows it.
        if (!_auth.IsAdmin) return;

        _hub = _hubFactory.Create("alerts");

        _hub.On<AlertTriggeredDto>("AlertTriggered", alert =>
        {
            lock (_recentGate)
            {
                _recent.Insert(0, alert);
                if (_recent.Count > MaxRecent)
                    _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
            }
            Interlocked.Increment(ref _unreadCount);
            OnChange?.Invoke();
        });

        _hub.On<ServerOfflineNotificationPayload>("ServerOffline", payload =>
        {
            OnServerOffline?.Invoke(payload);
        });

        // Group membership is per-connection and lost on auto-reconnect - re-join so alerts keep flowing.
        _hub.RejoinOnReconnect(() => _hub.InvokeAsync("JoinAlertGroup"));

        try
        {
            await _hub.StartAsync().ConfigureAwait(false);
            await _hub.InvokeAsync("JoinAlertGroup").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Hub not available - silently degrade
            System.Diagnostics.Debug.WriteLine($"[AlertNotification] Hub start failed: {ex.Message}");
        }
    }

    public void ClearUnread()
    {
        Interlocked.Exchange(ref _unreadCount, 0);
        OnChange?.Invoke();
    }

    /// <summary>S-FEAT-ALCL: empties the in-session "recent alerts" buffer (and resets the unread badge).
    /// The buffer is client-side only, so this just clears what this session has accumulated.</summary>
    public void ClearRecent()
    {
        lock (_recentGate) _recent.Clear();
        Interlocked.Exchange(ref _unreadCount, 0);
        OnChange?.Invoke();
    }

    /// <summary>Ends the current user's subscription and clears its in-memory alert projection.</summary>
    public async Task StopAsync()
    {
        var hub = _hub;
        _hub = null;
        if (hub is not null)
        {
            try { await hub.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AlertNotification] Hub dispose failed: {ex.Message}");
            }
        }

        ClearRecent();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
