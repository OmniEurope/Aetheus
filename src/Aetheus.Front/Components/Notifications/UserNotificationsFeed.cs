// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Notifications;

/// <summary>Shares the current user notification count and change event between the bell, menu and
/// inbox. Server pushes and mark-read actions reload the feed; polling catches up after missed
/// pushes.</summary>
public sealed class UserNotificationsFeed : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    public const int LatestCount = 5;

    private readonly ApiClient _api;
    private readonly AuthStateProvider _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<UserNotificationsFeed> _logger;
    /// <summary>Pushes arriving within this window after the first one are served by one
    /// refresh.</summary>
    public const int PushCoalesceMilliseconds = 1000;

    private readonly object _gate = new();
    private readonly TrailingReloadCoalescer _pushRefresh;
    private ITimer? _poll;
    private Task _startup = Task.CompletedTask;
    // FRONT-005: bumped whenever the signed-in identity changes (sign-out, another account). An answer
    // requested under an older generation belongs to the previous account and is dropped.
    private int _authGeneration;
    private string? _authUser;

    public UserNotificationsFeed(ApiClient api, AuthStateProvider auth, TimeProvider time, ILogger<UserNotificationsFeed> logger)
    {
        _api = api;
        _auth = auth;
        _time = time;
        _pushRefresh = new TrailingReloadCoalescer(PushCoalesceMilliseconds,
            (milliseconds, ct) => Task.Delay(TimeSpan.FromMilliseconds(milliseconds), time, ct));
        _logger = logger;
        _authUser = CurrentUser();
        _auth.OnAuthStateChanged += OnAuthStateChanged;
    }

    public int UnreadCount { get; private set; }

    /// <summary>The newest deliveries, for the bell's popover.</summary>
    public IReadOnlyList<NotificationDeliveryDto> Latest { get; private set; } = [];

    public event Action? OnChanged;

    /// <summary>Load the count and start polling on the first call. Concurrent callers await that
    /// same initial load rather than sending duplicate requests.</summary>
    public async Task EnsureStartedAsync()
    {
        if (!_auth.IsAuthenticated) return;
        Task load;
        lock (_gate)
        {
            if (_poll is null)
            {
                _poll = _time.CreateTimer(_ => _ = RefreshAsync(), null, PollInterval, PollInterval);
                _startup = RefreshAsync();
            }
            load = _startup;
        }
        await load.ConfigureAwait(false);
    }

    /// <summary>Coalesce notification pushes into one refresh per window without holding the hub
    /// message loop.</summary>
    public void RefreshFromPush() => _ = _pushRefresh.RequestBestEffortAsync(RefreshAsync, CancellationToken.None);

    /// <summary>Read and retain the count and newest deliveries independently so a failed list
    /// request cannot hide a successfully retrieved count.</summary>
    public async Task RefreshAsync()
    {
        if (!_auth.IsAuthenticated) return;
        var generation = Volatile.Read(ref _authGeneration);
        var countTask = ReadAsync(() => _api.Notifications.GetUnreadCountAsync(), "unread count");
        var latestTask = ReadAsync(() => _api.Notifications.GetMineAsync(1, LatestCount), "latest deliveries");
        await Task.WhenAll(countTask, latestTask).ConfigureAwait(false);
        var (countRead, count) = await countTask.ConfigureAwait(false);
        var (latestRead, latest) = await latestTask.ConfigureAwait(false);
        if (generation != Volatile.Read(ref _authGeneration) || !_auth.IsAuthenticated) return;
        // Best effort like the other top-bar surfaces: what could not be read keeps its last known value.
        if (countRead) UnreadCount = count;
        if (latestRead && latest is not null) Latest = latest.Items;
        if (countRead || latestRead) OnChanged?.Invoke();
    }

    private async Task<(bool Read, T? Value)> ReadAsync<T>(Func<Task<T>> read, string what)
    {
        try
        {
            return (true, await read().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "[UserNotifications] refresh of the {What} failed", what);
            return (false, default);
        }
    }

    public async Task<bool> MarkReadAsync(int deliveryId)
    {
        var ok = await _api.Notifications.MarkReadAsync(deliveryId).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
        return ok;
    }

    /// <summary>Marks everything read; false when the server refused, the count then stays as it was.</summary>
    public async Task<bool> MarkAllReadAsync()
    {
        var changed = await _api.Notifications.MarkAllReadAsync().ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
        return changed is not null;
    }

    private string? CurrentUser() => _auth.IsAuthenticated ? _auth.Username ?? string.Empty : null;

    private void OnAuthStateChanged()
    {
        var user = CurrentUser();
        if (!string.Equals(user, _authUser, StringComparison.Ordinal))
        {
            _authUser = user;
            Interlocked.Increment(ref _authGeneration);
        }
        if (_auth.IsAuthenticated) return;
        lock (_gate)
        {
            _poll?.Dispose();
            _poll = null;
            _startup = Task.CompletedTask;
        }
        // A count loaded for one user must not survive into the next session in the same browser.
        UnreadCount = 0;
        Latest = [];
        OnChanged?.Invoke();
    }

    public void Dispose()
    {
        _auth.OnAuthStateChanged -= OnAuthStateChanged;
        lock (_gate)
        {
            _poll?.Dispose();
            _poll = null;
        }
    }
}
