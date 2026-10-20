// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Front.Components.Shared;

/// <summary>Share one approvals list and hub connection between home and the top bar so pending
/// decisions remain visible across the session.</summary>
public sealed class PendingApprovalsService : IAsyncDisposable
{
    private readonly ApiClient _api;
    private readonly AuthStateProvider _auth;
    private readonly HubConnectionFactory _hubFactory;
    private readonly ILogger<PendingApprovalsService> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly TrailingReloadCoalescer _coalescer = new(500);
    private readonly CancellationTokenSource _lifetime = new();
    private HubConnection? _hub;

    public PendingApprovalsService(
        ApiClient api, AuthStateProvider auth, HubConnectionFactory hubFactory, ILogger<PendingApprovalsService> logger)
    {
        _api = api;
        _auth = auth;
        _hubFactory = hubFactory;
        _logger = logger;
        // A list loaded for one user must not survive into the next session in the same browser.
        _auth.OnAuthStateChanged += OnAuthStateChanged;
        _hubFactory.BackendReplaced += OnBackendReplaced;
    }

    public IReadOnlyList<PendingApprovalDto> Items { get; private set; } = [];

    public event Action? OnChanged;

    /// <summary>Loads the list, and on the first call opens the realtime subscription that keeps it
    /// current. Safe to call from every view that shows it.</summary>
    public async Task EnsureStartedAsync()
    {
        if (!_auth.IsAuthenticated) return;
        await ReloadAsync().ConfigureAwait(false);
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hub is not null) return;
            _hub = _hubFactory.Create("pipelines");
            _hub.On<int, string, string>("ApprovalRequired", (_, _, _) => RequestReloadAsync());
            _hub.On<int, string, string>("ApprovalResolved", (_, _, _) => RequestReloadAsync());
            _hub.On<int, PipelineStatus>("PipelineRunCompleted", (_, _) => RequestReloadAsync());
            _hub.On<int>("PipelineRunCancelled", _ => RequestReloadAsync());
            _hub.RejoinOnReconnect(async () =>
            {
                await _hub.InvokeAsync("JoinPipelineUpdatesGroup");
                await ReloadAsync();
            });
            await _hub.StartAsync().ConfigureAwait(false);
            await _hub.InvokeAsync("JoinPipelineUpdatesGroup").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or HubException)
        {
            // Best-effort like every other realtime surface: the list stays as loaded. HubException
            // included: the hub refuses JoinPipelineUpdatesGroup to a user with no readable pipeline,
            // and this service starts from the top bar, so letting it escape crashed the layout on
            // every page for such a user (QA run 2351, two E2E role tests).
            _logger.LogWarning(ex, "[PendingApprovals] realtime subscription failed");
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task ReloadAsync()
    {
        try
        {
            Items = _auth.IsAuthenticated ? await _api.Pipelines.GetPendingApprovalsAsync().ConfigureAwait(false) : [];
            OnChanged?.Invoke();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[PendingApprovals] reload failed");
        }
    }

    private Task RequestReloadAsync() => _coalescer.RequestAsync(ReloadAsync, _lifetime.Token);

    // Run 2458: after a blue-green switch the approval is pushed by the new colour while this socket is
    // still on the previous one. Restart it (same connection object, behind the start gate so a second
    // signal or a first start never overlaps it), re-join, and fetch the list once.
    private void OnBackendReplaced() => _ = MoveToCurrentBackendAsync();

    private async Task MoveToCurrentBackendAsync()
    {
        if (!_auth.IsAuthenticated || _lifetime.IsCancellationRequested) return;
        try
        {
            await _startGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (_hub is null) return;
            var hub = _hub;
            await hub.RestartOnCurrentBackendAsync(async () =>
            {
                await hub.InvokeAsync("JoinPipelineUpdatesGroup").ConfigureAwait(false);
                await ReloadAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or HubException)
        {
            _logger.LogWarning(ex, "[PendingApprovals] moving to the new backend failed");
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void OnAuthStateChanged()
    {
        if (_auth.IsAuthenticated) return;
        Items = [];
        OnChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _auth.OnAuthStateChanged -= OnAuthStateChanged;
        _hubFactory.BackendReplaced -= OnBackendReplaced;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_hub is not null)
            await _hub.DisposeAsync().ConfigureAwait(false);
        _startGate.Dispose();
        _lifetime.Dispose();
    }
}
