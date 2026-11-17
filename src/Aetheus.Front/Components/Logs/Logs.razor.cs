// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Front.Components.Logs;

public partial class Logs : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;

    private HubConnection? _hubConnection;
    private readonly List<TaskLogDto> _logs = [];
    internal int? _taskIdFilter;
    private int? _currentWatchId;
    private ElementReference _logContainer;
    private bool _showUnmasked;

    protected override void OnInitialized()
    {
        Breadcrumb.Set(new BreadcrumbItem(L["LiveLogs"]));
    }

    internal async Task StartWatching()
    {
        if (_taskIdFilter is null) return;

        if (_hubConnection is null)
        {
            _hubConnection = HubFactory.Create("logs");

            _hubConnection.On<TaskLogDto>("LogReceived", log =>
                InvokeAsync(() =>
                {
                    AppendLog(log);
                    StateHasChanged();
                }));

            // F20: server now emits "LogsReceived" with a batch per TaskId.
            _hubConnection.On<List<TaskLogDto>>("LogsReceived", logs =>
                InvokeAsync(() =>
                {
                    foreach (var log in logs) AppendLog(log);
                    StateHasChanged();
                }));

            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                if (_currentWatchId is { } watchId)
                    await _hubConnection.InvokeAsync("JoinTaskGroup", watchId);
            }));

            await _hubConnection.StartAsync();
        }

        var nextWatchId = _taskIdFilter.Value;
        if (_currentWatchId == nextWatchId) return;
        if (_currentWatchId.HasValue)
            await _hubConnection.InvokeAsync("LeaveTaskGroup", _currentWatchId.Value);

        ResetWatchState(nextWatchId);
        await _hubConnection.InvokeAsync("JoinTaskGroup", nextWatchId);
        var snapshot = await Api.Monitoring.GetTaskLogsAsync(nextWatchId);
        if (_currentWatchId != nextWatchId) return;

        // Join first so no live line can fall into the gap between the snapshot and subscription,
        // then merge the snapshot with any line received while the HTTP request was in flight.
        var merged = snapshot
            .Concat(_logs)
            .GroupBy(log => log.Id)
            .Select(group => group.Last())
            .OrderBy(log => log.Timestamp)
            .TakeLast(5000)
            .ToList();
        _logs.Clear();
        _logs.AddRange(merged);
    }

    private void ResetWatchState(int nextWatchId)
    {
        _logs.Clear();
        _showUnmasked = false;
        _currentWatchId = nextWatchId;
    }

    private void AppendLog(TaskLogDto log)
    {
        if (_currentWatchId is null || log.TaskId != _currentWatchId.Value) return;
        _logs.Add(log);
        if (_logs.Count > 5000) _logs.RemoveAt(0);
    }

    private async Task ToggleUnmasked()
    {
        if (_currentWatchId is null) return;
        var watchId = _currentWatchId.Value;
        var showUnmasked = !_showUnmasked;

        var logs = showUnmasked
            ? await Api.Monitoring.GetTaskLogsUnmaskedAsync(watchId)
            : await Api.Monitoring.GetTaskLogsAsync(watchId);
        if (_currentWatchId != watchId) return;

        _showUnmasked = showUnmasked;
        _logs.Clear();
        _logs.AddRange(logs);
    }

    private static string HighlightMasked(string message)
    {
        var encoded = WebUtility.HtmlEncode(message);
        return encoded.Replace("***", "<span class=\"masked-value\">***</span>", StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            if (_currentWatchId is { } watchId)
            {
                try { await _hubConnection.InvokeAsync("LeaveTaskGroup", watchId); }
                catch { /* best-effort */ }
            }
            await _hubConnection.DisposeAsync();
        }
    }
}
