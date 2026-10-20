// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Subscribes to the per-user <c>/hubs/user</c> channel and raises <see cref="OnPermissionsChanged"/>
/// when the backend pushes <c>PermissionsChanged(reason)</c> (an admin changed the current user's
/// roles, org membership, or a role's permissions). A short debounce coalesces the burst of events a
/// bulk change produces into a single prompt. The UI subscriber (NavMenu) shows a blocking dialog
/// whose only action is a full reload. Degrades silently if the hub is unavailable, mirroring the
/// other realtime services.
/// </summary>
public sealed class UserNotificationService(
    HubConnectionFactory hubFactory,
    AuthStateProvider auth,
    ApiClient api,
    PermissionService permissions,
    TimeProvider timeProvider,
    Aetheus.Front.Components.Notifications.UserNotificationsFeed? notificationsFeed = null) : IAsyncDisposable
{
    private HubConnection? _hub;
    private ITimer? _debounce;
    private readonly object _gate = new();
    private CancellationTokenSource? _startRetryCts;

    private static IReadOnlyList<TimeSpan> RetryDelays => FrontendRuntimeDefaults.SignalRRetryDelays;

    /// <summary>Debounce window (ms) that coalesces a burst of change events into one prompt.
    /// Internal setter so tests can drive it deterministically.</summary>
    internal int DebounceMilliseconds { get; set; } = 500;

    public event Action? OnPermissionsChanged;

    public async Task StartAsync()
    {
        if (_hub is not null) return;
        if (!auth.IsAuthenticated) return;

        _hub = hubFactory.Create("user");
        _hub.On<string>("PermissionsChanged", reason => HandleServerMessage(reason));
        // Refresh the shared user feed for recorded, read or added notifications so the bell, menu
        // and page stay consistent; coalesce bursts into one reload.
        _hub.On("NotificationsChanged", () =>
        {
            notificationsFeed?.RefreshFromPush();
            return Task.CompletedTask;
        });
        _hub.Reconnected += OnReconnectedAsync;

        if (await TryConnectAsync().ConfigureAwait(false))
            return;

        // Retry an initially unavailable hub on the normal backoff schedule because reconnect
        // callbacks require a connection to have succeeded first.
        _startRetryCts = new CancellationTokenSource();
        _ = InitialRetryLoopAsync(_startRetryCts.Token);
    }

    private async Task<bool> TryConnectAsync()
    {
        if (_hub is null) return false;
        try
        {
            await _hub.StartAsync().ConfigureAwait(false);
            // Reconcile once right after a successful (re)connect - covers both the very first
            // connect and a retried one - so events missed while disconnected are never silently lost.
            await ReconcileAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Hub not available - silently degrade (realtime is best-effort).
            System.Diagnostics.Debug.WriteLine($"[UserNotification] Hub start failed: {ex.Message}");
            return false;
        }
    }

    private async Task InitialRetryLoopAsync(CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested && _hub is not null && auth.IsAuthenticated)
        {
            var delay = RetryDelays[Math.Min(attempt, RetryDelays.Count - 1)];
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested || _hub is null) return;

            if (await TryConnectAsync().ConfigureAwait(false))
            {
                // Connected: retire the retry CTS instead of leaking it until the next Stop/Dispose.
                _startRetryCts?.Dispose();
                _startRetryCts = null;
                return;
            }
            attempt++;
        }
    }

    // The hub callback and the reconnect reconciliation funnel through here (also the test seam).
    internal void HandleServerMessage(string reason) => ScheduleNotify();

    private void ScheduleNotify()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = timeProvider.CreateTimer(_ => Fire(), null,
                TimeSpan.FromMilliseconds(DebounceMilliseconds), Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire() => OnPermissionsChanged?.Invoke();

    private Task OnReconnectedAsync(string? connectionId) => ReconcileAsync();

    private async Task ReconcileAsync()
    {
        // Events fired while disconnected are lost; reconcile by re-fetching and prompting only if
        // the effective permission set actually differs from what the client currently holds.
        try
        {
            var summary = await api.Auth.GetMyPermissionsAsync().ConfigureAwait(false);
            if (summary is null) return;
            // A page loaded while the backend was still starting never got its permissions: there is
            // nothing to compare with, and the full set is not a change. Load it instead of prompting
            // (the "Vos droits ont été modifiés" dialog opened after every local restart).
            if (!permissions.IsLoaded)
            {
                permissions.SetPermissions(summary.EffectivePermissions, auth.IsAdmin);
                return;
            }
            if (HasChanged(summary.EffectivePermissions))
                Fire();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserNotification] Reconcile failed: {ex.Message}");
        }
    }

    private bool HasChanged(List<EffectivePermissionDto> latest)
        => EffectivePermissionsChanged(permissions.GetPermissions(), latest);

    internal static bool EffectivePermissionsChanged(
        IReadOnlyCollection<EffectivePermissionDto> current,
        IReadOnlyCollection<EffectivePermissionDto> latest)
    {
        if (current.Count != latest.Count) return true;
        var currentKeys = current.Select(Key).ToHashSet();
        return latest.Any(p => !currentKeys.Contains(Key(p)));

        static string Key(EffectivePermissionDto p) => $"{p.ResourceType}:{p.ResourceId}:{p.Permission}";
    }

    /// <summary>Ends the current user's subscription and cancels every deferred notification.</summary>
    public async Task StopAsync()
    {
        _startRetryCts?.Cancel();
        _startRetryCts?.Dispose();
        _startRetryCts = null;
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
        var hub = _hub;
        _hub = null;
        if (hub is not null)
        {
            try { await hub.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UserNotification] Hub dispose failed: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
