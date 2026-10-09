// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Notifications;

/// <summary>
/// R-115: the single source of the current user's notifications in the browser. The header bell, the
/// menu badge and the /notifications page all read <see cref="UnreadCount"/> from here and listen to
/// <see cref="OnChanged"/>, so they can never disagree. A singleton for the session, like
/// <c>PendingApprovalsService</c>.
///
/// Refresh: the backend pushes <c>NotificationsChanged</c> on <c>/hubs/user</c> for every delivery
/// recorded or read (R-182, wired in <c>UserNotificationService</c>), which reloads this feed at
/// once. The feed also reloads after every mark-read action here, and a slow <see cref="PollInterval"/>
/// catch-up remains only for pushes missed while the hub was disconnected.
/// </summary>
public sealed class UserNotificationsFeed : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    public const int LatestCount = 5;

    private readonly ApiClient _api;
    private readonly AuthStateProvider _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<UserNotificationsFeed> _logger;
    /// <summary>R-461: pushes arriving within this window after the first one are served by one refresh.</summary>
    public const int PushCoalesceMilliseconds = 1000;

    private readonly object _gate = new();
    private readonly TrailingReloadCoalescer _pushRefresh = new(PushCoalesceMilliseconds);
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
        _logger = logger;
        _authUser = CurrentUser();
        _auth.OnAuthStateChanged += OnAuthStateChanged;
    }

    public int UnreadCount { get; private set; }

    /// <summary>The newest deliveries, for the bell's popover.</summary>
    public IReadOnlyList<NotificationDeliveryDto> Latest { get; private set; } = [];

    public event Action? OnChanged;

    /// <summary>
    /// Loads the count and starts the poll on the first call. Safe to call from every view: R-461, the
    /// bell and the menu entry both start the feed when the layout loads, and used to send the pair of
    /// requests twice; a later call now waits for the first load instead of sending its own.
    /// </summary>
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

    /// <summary>
    /// R-461: a <c>NotificationsChanged</c> push. A burst of pushes (several deliveries recorded at once)
    /// is served by one refresh at the end of <see cref="PushCoalesceMilliseconds"/>, not one pair of
    /// requests per push. Returns at once, so the hub's message loop is never held.
    /// </summary>
    public void RefreshFromPush() => _ = _pushRefresh.RequestBestEffortAsync(RefreshAsync, CancellationToken.None);

    /// <summary>
    /// Reloads the count and the newest deliveries. Recette R2-106: the two are read and kept
    /// independently. The count used to be set only once both answers were in, so a list that could not
    /// be read (an error answer, a body that does not parse, a timeout) left the bell without its number
    /// while the count itself had been read fine; and a failure other than an HTTP error escaped the
    /// first load, which every later view of the bell then re-threw.
    /// </summary>
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
