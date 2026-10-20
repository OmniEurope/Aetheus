// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Layout;

public partial class MainLayout : IAsyncDisposable
{
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private TimeProvider TimeProvider { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HelpService Help { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private ILogger<MainLayout> Logger { get; set; } = default!;
    [Inject] private ActiveOrganizationService Orgs { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;
    [Inject] private IHttpClientFactory HttpFactory { get; set; } = default!;
    [Inject] private ApplicationVersionState VersionState { get; set; } = default!;
    [Inject] private TaskTrackerService TaskTracker { get; set; } = default!;
    [Inject] private RealtimeSessionLifecycle RealtimeSession { get; set; } = default!;
    [Inject] private IWebAssemblyHostEnvironment HostEnv { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    private bool _sidebarExpanded;
    private bool _initialized;
    private bool _splashHidden;

    private bool _isMobile;
    private bool _viewportKnown;
    internal bool IsSidebarExpanded => _sidebarExpanded;
    internal bool IsMobileViewport => _isMobile;
    internal bool IsViewportKnown => _viewportKnown;
    private bool _desktopSidebarExpanded = true;
    private DotNetObjectReference<MainLayout>? _selfRef;
    // A page come back to finds its scroll position again.
    private PageScrollMemory? _scrollMemory;

    private bool _appReady;
    private bool AppReady => _appReady;
    private bool _authenticatedSessionReady;
    private bool AuthenticatedChromeReady => Auth.IsAuthenticated && _authenticatedSessionReady;
    private bool CanAccessCurrentRoute => RouteAccessPolicy.CanAccess(
        Nav.ToBaseRelativePath(Nav.Uri),
        Auth.IsAuthenticated,
        Auth.IsAdmin,
        Permissions);
    [Inject] private Aetheus.Front.Components.Settings.UserPreferenceSession PreferenceSession { get; set; } = default!;
    [Inject] private IUserPreferences Preferences { get; set; } = default!;

    /// <summary>STD-SHELL: the sidebar floats over the page below 64rem (phones and tablets, ASTRAIA parity)
    /// and is pushed beside it above; both toggles follow it for their glyph.</summary>
    internal OmniSidebarReveal SidebarReveal => _isMobile ? OmniSidebarReveal.Overlay : OmniSidebarReveal.Push;

    /// <summary>The languages of the application menu; the code is the value stored for the next start.</summary>
    private IReadOnlyList<OmniAppMenuLanguage> Languages =>
        [new(FrenchLanguage, L["LanguageFrench"]), new(EnglishLanguage, L["LanguageEnglish"])];

    private const string FrenchLanguage = "fr-FR";
    private const string EnglishLanguage = "en";

    internal static string CurrentLanguage =>
        System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("fr", StringComparison.OrdinalIgnoreCase)
            ? FrenchLanguage
            : EnglishLanguage;

    private bool _isProduction = true;
    private string _envName = "Production";
    private string? _devBranch;
    private string? _devLabel;
    private LoggedErrorBoundary? _errorBoundary;
    private bool _userMenuOpen;
    private int ActiveOrganizationId => Orgs.Active?.Id ?? 0;
    private IReadOnlyList<OmniOption<int>> ActiveOrganizationOptions =>
        Orgs.Available.Select(organization => new OmniOption<int>(organization.Id, organization.Name)).ToArray();
    private string _version = "dev";
    internal bool _newVersionAvailable;
    private ApplicationVersionMonitor? _versionMonitor;
    private Microsoft.AspNetCore.SignalR.Client.HubConnectionState _signalRState = Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected;
    private bool? _backendLive;
    internal bool BackendConnected =>
        _signalRState == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected
        || _backendLive == true;

    private bool _showOfflineDialog;
    private readonly ReconnectCountdownTicker _countdown = new(TimeProvider.System);
    private bool _manualReconnecting;
    private bool _wasConnectedOnce;
    private bool _connectAttempted;
    private bool _disposed;
    private CancellationTokenSource? _offlineDelayCts;


    protected override async Task OnInitializedAsync()
    {
        _isProduction = HostEnv.IsProduction();
        _envName = HostEnv.Environment;
        _devBranch = Configuration["DevBanner:Branch"];
        _devLabel = Configuration["DevBanner:Label"];

        await Auth.InitializeAsync();
        await Auth.WatchStorageAsync();
        _initialized = true;
        // D48: a session that could not survive the start-up (expired, nothing to renew it with).
        if (Auth.LastSessionEndReason is not null)
            AnnounceSessionEnd();
        Auth.OnAuthStateChanged += OnAuthStateChanged;
        Auth.OnNeedsLogin += OnNeedsLogin;
        Nav.LocationChanged += OnLocationChanged;
        _scrollMemory = new PageScrollMemory(Nav, JS);
        TaskTracker.OnConnectionStateChanged += OnSignalRStateChanged;
        TaskTracker.OnReconnectAttempt += OnReconnectAttempt;
        _signalRState = TaskTracker.ConnectionState;
        _wasConnectedOnce = _signalRState == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected;

        // R1-66: the preferences follow the user found at start (or the signed-out defaults) before
        // anything reads them; the active organization below is one of them.
        await PreferenceSession.StartAsync();
        _sessionIdentity = CurrentIdentity;

        // A forced password change holds the user on its own screen, and the backend refuses everything else
        // until then (SITE-SEC-001): the session starts after the change, from the next login.
        if (Auth.IsAuthenticated && !Auth.MustChangePassword)
        {
            // Same start as after a later login: permissions and organizations, then the realtime hub.
            Orgs.Changed += OnOrgsChanged;
            _connectAttempted = true;
            // The hub is started, not awaited: its outcome arrives through OnSignalRStateChanged.
            await MainLayoutBootstrapLoader.StartAuthenticatedSessionAsync(
                Api, Permissions, Auth, Orgs, TaskTracker, Logger);
            _signalRState = TaskTracker.ConnectionState;
        }
        _authenticatedSessionReady = Auth.IsAuthenticated && !Auth.MustChangePassword;

        RedirectIfUnauthenticated();
        _appReady = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _version = Configuration["App:Version"] ?? "dev";
            await JS.InvokeVoidAsync("Aetheus.lockTitle", $"Aetheus v{_version}");
            if (!string.IsNullOrWhiteSpace(_devLabel))
                await JS.InvokeVoidAsync("Aetheus.setTitlePrefix", _devLabel);
            await JS.InvokeVoidAsync("Aetheus.initFormShortcuts");
            _selfRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("Aetheus.watchViewport", _selfRef);
            await JS.InvokeVoidAsync("Aetheus.watchConnectivity", _selfRef);
            _versionMonitor = new ApplicationVersionMonitor(
                HttpFactory, new Uri(Nav.BaseUri), _version, OnNewVersionAvailableAsync);
            _versionMonitor.Start();
        }

        if (AppReady && _viewportKnown && !_splashHidden)
        {
            _splashHidden = true;
            // R3-29: a page opened on a pasted address gets its logical parent behind it in the history.
            if (Auth.IsAuthenticated && !Auth.MustChangePassword)
                await DeepLinkParentEntry.SeedAsync(Nav, JS);
            await JS.InvokeVoidAsync("Aetheus.hideSplash");
        }
    }

    private Task<bool> CheckForNewVersionAsync(HttpClient http, CancellationToken ct) =>
        ApplicationVersionMonitor.CheckAsync(http, _version, OnNewVersionAvailableAsync, ct);

    private Task OnNewVersionAvailableAsync()
    {
        _newVersionAvailable = true;
        VersionState.MarkNewVersionAvailable();
        Cache.Invalidate(ListCacheService.ApiReferenceKey);
        // The sockets already open are still on the previous colour: move the realtime owners that
        // follow it (approvals, run page) to the backend that is now routed. The task tracker, which
        // drives the offline overlay, is deliberately not among them, so nothing flashes here.
        HubFactory.AnnounceBackendReplaced();
        return InvokeAsync(StateHasChanged);
    }

    internal void OnReloadClick()
    {
        Nav.NavigateTo(Nav.Uri, forceLoad: true);
    }

    /// <summary>Who the session was started for (user id and name), or null while signed out.</summary>
    private string? CurrentIdentity => Auth.IsAuthenticated ? $"{Auth.UserId}|{Auth.Username}" : null;

    private string? _sessionIdentity;

    private void OnAuthStateChanged()
    {
        // R1-66 with OE 1.8.1: another account took over this page (another tab signed in as someone
        // else). The page is not reloaded: the session of the previous account is closed (its page,
        // lists, permissions, organizations and realtime connections) and started again for the new
        // one, its preferences read again for it, before any page shows again.
        if (Auth.IsAuthenticated && _authenticatedSessionReady && CurrentIdentity != _sessionIdentity)
        {
            _ = InvokeAsync(SwitchAccountAsync);
            return;
        }

        if (!Auth.IsAuthenticated)
        {
            _authenticatedSessionReady = false;
            _sessionIdentity = null;
            ClearSessionState();
            ResetConnectionLostState();
            // The signed-out page keeps no preference of the account that left.
            _ = InvokeAsync(() => PreferenceSession.StartAsync());
        }
        else if (!_authenticatedSessionReady)
        {
            _ = InvokeAsync(InitializeAuthenticatedSessionAsync);
        }
        RedirectIfUnauthenticated();
        StateHasChanged();
    }

    private async Task SwitchAccountAsync()
    {
        // The chrome and the routed page leave the screen at once (the session loader takes their
        // place), so nothing loaded for the previous account stays visible while the new session starts.
        _authenticatedSessionReady = false;
        ClearSessionState();
        StateHasChanged();
        await RealtimeSession.StopAsync();
        ResetConnectionLostState();
        await InitializeAuthenticatedSessionAsync();
    }

    private async Task InitializeAuthenticatedSessionAsync()
    {
        if (!Auth.IsAuthenticated || _authenticatedSessionReady) return;
        try
        {
            // R1-66: the preferences are the signed-in user's before anything reads them (the active
            // organization is one of them).
            await PreferenceSession.StartAsync();
            _sessionIdentity = CurrentIdentity;
            if (Auth.MustChangePassword) return;
            Orgs.Changed -= OnOrgsChanged;
            Orgs.Changed += OnOrgsChanged;
            _connectAttempted = true;
            await MainLayoutBootstrapLoader.StartAuthenticatedSessionAsync(
                Api, Permissions, Auth, Orgs, TaskTracker, Logger);
            _signalRState = TaskTracker.ConnectionState;
            _authenticatedSessionReady = Auth.IsAuthenticated;
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    // F-41: single point of truth for handling 401s. Bouncing all unauth events through here
    // (instead of letting each HTTP handler call NavigateTo) avoids redirect loops when several
    // requests fail in parallel.
    private void OnNeedsLogin()
    {
        _ = InvokeAsync(async () =>
        {
            await RealtimeSession.StopAsync();
            // A rejected token logs AuthStateProvider out before raising this event. Clear every
            // session-scoped projection here too, otherwise a second account can briefly receive
            // the previous account's cached rows while its first requests revalidate.
            ClearSessionState();
            ResetConnectionLostState();

            // S5TK: say why the user was bounced. Before the page check: the logout's OnAuthStateChanged
            // has usually redirected to /login already, which silenced the toast when tied to it.
            AnnounceSessionEnd();
            var uri = Nav.ToBaseRelativePath(Nav.Uri);
            if (!uri.StartsWith("login", StringComparison.OrdinalIgnoreCase))
                Nav.NavigateTo(LoginRedirect.ToLogin(Nav));
        });
    }

    private void AnnounceSessionEnd() => SessionEndAnnouncement.Announce(Auth, Notify, L, Api, Logger);

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        _errorBoundary?.Recover();
        _userMenuOpen = false;
        RedirectIfUnauthenticated();
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>Called by Aetheus.watchViewport on boot and on every 1024px crossing. The initial
    /// callback is part of the splash handoff, preventing an incorrect sidebar frame from becoming
    /// visible during a cold load.</summary>
    [JSInvokable]
    public async Task OnViewportChanged(bool mobile)
    {
        var viewportModeChanged = _viewportKnown && _isMobile != mobile;
        _isMobile = mobile;
        if (!_viewportKnown || viewportModeChanged)
            _sidebarExpanded = mobile ? false : _desktopSidebarExpanded;
        _viewportKnown = true;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Called by Aetheus.watchConnectivity when the browser reports the network is back, or when the
    /// tab becomes visible again after the machine woke up. Both are the moment a scheduled backoff
    /// step became pointless: it was scheduled for a network that no longer applies.
    /// </summary>
    [JSInvokable]
    public void OnConnectivityWake()
    {
        TaskTracker.RequestImmediateReconnect();
        // 2026-10-06: every live view fetches what it may have missed while the page was away.
        HubFactory.AnnouncePageResumed();
    }

    private void OnSidebarOpenChanged(bool open)
    {
        _sidebarExpanded = open;
        if (!_isMobile)
            _desktopSidebarExpanded = open;
    }

    private void RedirectIfUnauthenticated()
    {
        var uri = Nav.ToBaseRelativePath(Nav.Uri);
        if (!Auth.IsAuthenticated)
        {
            if (!uri.StartsWith("login", StringComparison.OrdinalIgnoreCase))
                Nav.NavigateTo(LoginRedirect.ToLogin(Nav));
            return;
        }

        // Authenticated but flagged for a forced password change: gate ALL navigation onto the
        // mandatory change-password screen until a fresh token (without the claim) is issued.
        if (Auth.MustChangePassword
            && !uri.StartsWith("account/change-password", StringComparison.OrdinalIgnoreCase))
            Nav.NavigateTo(LoginRedirect.ToChangePassword(Nav));
    }

    private Aetheus.Front.Components.Settings.SiteAppearanceWindow? _appearanceWindow;

    /// <summary>The menu's Theme row (OE closes the menu first) opens the appearance
    /// window.</summary>
    private async Task OnThemeMenuClick()
    {
        if (_appearanceWindow is not null)
        {
            await _appearanceWindow.OpenAsync();
        }
    }

    /// <summary>The language picked in the menu is stored for the next start, then the page reloads in it.</summary>
    internal async Task OnLanguageChanged(string language)
    {
        if (string.Equals(language, CurrentLanguage, StringComparison.Ordinal)) return;
        await JS.InvokeVoidAsync("localStorage.setItem", StorageKeys.Lang, language);
        await Preferences.SetAsync(PreferenceKeys.Language, language);
        await JS.InvokeVoidAsync("Aetheus.setLang", language);
        Nav.NavigateTo(Nav.Uri, forceLoad: true);
    }

    private void OnSettingsMenuClick() => Nav.NavigateTo("/settings");

    internal void RecoverError()
    {
        _errorBoundary?.Recover();
    }

    internal async Task OnLogout()
    {
        ClearSessionState();
        // Stop and reset every realtime owner while the token is still valid. This prevents both a
        // reconnect race during logout and per-user state leaking into a same-runtime re-login.
        await RealtimeSession.StopAsync();
        ResetConnectionLostState();
        // While the token is still held, so the server forgets the mailbox passwords it keeps for
        // us.
        await Api.Auth.NotifyLogoutAsync();
        await Auth.LogoutAsync();
        Nav.NavigateTo("/login");
    }

    private void ClearSessionState()
    {
        Permissions.Clear();
        Cache.Clear();
        Orgs.Clear();
    }
    internal void NavigateToHelp()
    {
        var relative = Nav.ToBaseRelativePath(Nav.Uri);
        var pageKey = Help.ResolvePageKey(relative);
        Nav.NavigateTo(pageKey is not null ? $"/help/{pageKey}" : "/help");
    }

    private void ResetConnectionLostState()
    {
        _offlineDelayCts?.Cancel();
        _offlineDelayCts?.Dispose();
        _offlineDelayCts = null;
        _countdown.Stop();
        _signalRState = Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected;
        _backendLive = null;
        _showOfflineDialog = false;
        _manualReconnecting = false;
        _wasConnectedOnce = false;
        _connectAttempted = false;
    }

    private void OnSignalRStateChanged()
    {
        _signalRState = TaskTracker.ConnectionState;
        var connected = _signalRState == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected;
        if (connected)
            _backendLive = true;
        else
            _ = RefreshBackendLivenessAsync();
        _ = HandleConnectionStateChanged(connected);
    }

    private async Task<bool> RefreshBackendLivenessAsync(CancellationToken cancellationToken = default)
    {
        var live = await Api.Auth.IsBackendLiveAsync(cancellationToken);
        if (_disposed || cancellationToken.IsCancellationRequested) return live;
        await InvokeAsync(() =>
        {
            _backendLive = live;
            if (live)
                _showOfflineDialog = false;
            StateHasChanged();
        });
        return live;
    }

    // On reconnect, tear down the countdown and close the dialog. On a drop, wait briefly before
    // declaring the backend unavailable so quick SignalR interruptions never flash a false alert. The
    // returned task is that handling, to its end (the grace delay and the liveness probe included).
    private Task HandleConnectionStateChanged(bool connected)
    {
        return InvokeAsync(async () =>
        {
            if (connected)
            {
                _offlineDelayCts?.Cancel();
                _offlineDelayCts?.Dispose();
                _offlineDelayCts = null;
                _countdown.Stop();

                if (_showOfflineDialog)
                {
                    _showOfflineDialog = false;
                    _manualReconnecting = false;
                }

                _wasConnectedOnce = true;
                StateHasChanged();
                return;
            }

            if (_wasConnectedOnce || _connectAttempted)
            {
                _offlineDelayCts?.Cancel();
                _offlineDelayCts?.Dispose();
                _offlineDelayCts = new CancellationTokenSource();
                var token = _offlineDelayCts.Token;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), TimeProvider, token);
                    if (!token.IsCancellationRequested && !_disposed)
                    {
                        // A reconnect during the probe cancels it, and a cancelled probe answers "not
                        // live": without the second check the overlay opened on a page that was
                        // connected again, and stayed (QA 2448, SignalRDrop E2E).
                        if (await RefreshBackendLivenessAsync(token) || token.IsCancellationRequested || _disposed)
                            return;
                        _showOfflineDialog = true;
                        StateHasChanged();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Reconnected before the delay elapsed.
                }
            }
        });
    }

    private void OnReconnectAttempt(int delaySeconds)
    {
        _ = RefreshBackendLivenessAsync();
        _ = InvokeAsync(() => _countdown.RunAsync(delaySeconds, () =>
        {
            if (!_disposed) StateHasChanged();
            return Task.CompletedTask;
        }));
    }

    private async Task OnManualReconnect()
    {
        _countdown.Stop();
        _manualReconnecting = true;
        StateHasChanged();

        var outcome = await TaskTracker.ReconnectAsync();
        _manualReconnecting = false;

        // An expired session cannot reconnect; direct the user to sign in rather than retrying an
        // unauthenticated connection.
        if (outcome == TaskTrackerService.ReconnectOutcome.SessionExpired)
        {
            RedirectIfUnauthenticated();
            return;
        }

        if (TaskTracker.ConnectionState == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
            _showOfflineDialog = false;

        // TaskTracker schedules subsequent attempts and drives the countdown through
        // OnReconnectAttempt when this immediate attempt cannot reconnect.
        StateHasChanged();
    }

    private void DisposeManagedState()
    {
        if (_disposed) return;
        _disposed = true;
        Auth.OnAuthStateChanged -= OnAuthStateChanged;
        Auth.OnNeedsLogin -= OnNeedsLogin;
        Nav.LocationChanged -= OnLocationChanged;
        _scrollMemory?.Dispose();
        Orgs.Changed -= OnOrgsChanged;
        if (TaskTracker is not null)
        {
            TaskTracker.OnConnectionStateChanged -= OnSignalRStateChanged;
            TaskTracker.OnReconnectAttempt -= OnReconnectAttempt;
        }
        _offlineDelayCts?.Cancel();
        _offlineDelayCts?.Dispose();
        _countdown.Dispose();
        _versionMonitor?.Dispose();
    }

    public void Dispose()
    {
        DisposeManagedState();
        _selfRef?.Dispose();
        _selfRef = null;
    }

    public async ValueTask DisposeAsync()
    {
        DisposeManagedState();
        try
        {
            await JS.InvokeVoidAsync("Aetheus.disposeViewportWatcher");
            await JS.InvokeVoidAsync("Aetheus.disposeConnectivityWatcher");
        }
        catch (JSDisconnectedException)
        {
            // The browser runtime is already gone; there is no watcher left to detach.
        }
        finally
        {
            _selfRef?.Dispose();
            _selfRef = null;
        }
    }

    private void OnOrgsChanged() => InvokeAsync(StateHasChanged);

    internal Task OnActiveOrgChanged(object value)
    {
        // ORG6: the org picker lives inside the user submenu - close it on selection so the menu
        // doesn't linger open through the Orgs.SetActiveAsync reload.
        _userMenuOpen = false;
        if (value is not int id) return Task.CompletedTask;
        // Org-scoped lists (and any other session-cached data) belong to the previous organization -
        // drop the cache so the switch re-fetches under the new org instead of serving stale entries.
        Cache.Clear();
        return Orgs.SetActiveAsync(id);
    }
}
