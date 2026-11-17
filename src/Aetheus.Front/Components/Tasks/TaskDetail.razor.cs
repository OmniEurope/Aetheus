// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Aetheus.Front.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Components.Tasks;

public partial class TaskDetail : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private TaskTrackerService LiveTasks { get; set; } = default!;
    [Inject] private ILogger<TaskDetail> Logger { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    /// <summary>The address: a number or a UUID. One route takes both, Blazor's router calls two routes that differ
    /// only by their constraint ambiguous and refuses to start.</summary>
    [Parameter] public string? TaskKey { get; set; }

    [Parameter] public int TaskId { get; set; }

    /// <summary>ADR-060: the canonical address. <see cref="TaskId"/> is then 0 until the task is read.</summary>
    [Parameter] public Guid? TaskUid { get; set; }

    private ServerTaskDto? _task;
    private List<TaskLogDto> _logs = [];
    private bool _loading;
    private object? _loadedKey;

    // Subscribe to the task log group while the agent runs and obtain status from the shared live
    // task list.
    private HubConnection? _logHub;
    private int? _liveTaskId;
    private bool _followingLiveTasks;

    /// <summary>The task's internal key, whichever address opened the page: logs and hubs work on it.</summary>
    private int CurrentTaskId => _task?.Id ?? TaskId;

    private string TaskLabel => _task is not null || TaskId > 0
        ? $"{L["Task"]} #{_task?.DisplayNumber ?? TaskId}"
        : L["Task"].Value;

    /// <summary>True until the agent has reported the task's end: more lines may come.</summary>
    internal bool InFlight => _task?.Status
        is TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned or TaskExecutionStatus.Running;

    private bool CanCancel => _task is not null
        && _task.Status is TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned or TaskExecutionStatus.Running
        && Permissions.CanWrite(ResourceType.Server, _task.ServerId);

    protected override void OnInitialized()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
    }

    /// <summary>A key that is neither reads as task 0, which the server does not know: the page says not found.</summary>
    private void ReadTaskKey()
    {
        if (TaskKey is null) return;
        TaskUid = Guid.TryParse(TaskKey, out var uid) ? uid : null;
        TaskId = int.TryParse(TaskKey, out var id) ? id : 0;
    }

    protected override async Task OnParametersSetAsync()
    {
        ReadTaskKey();
        object key = TaskUid is { } uid ? uid : TaskId;
        if (Equals(_loadedKey, key))
        {
            SetBreadcrumb();
            return;
        }

        _loadedKey = key;
        _loading = true;
        _task = null;
        _logs = [];
        SetBreadcrumb();
        try
        {
            _task = TaskUid is { } taskUid
                ? await Api.Pipelines.GetTaskAsync(taskUid)
                : await Api.Pipelines.GetTaskAsync(TaskId);
            if (_task is not null)
                _logs = await Api.Monitoring.GetTaskLogsAsync(_task.Id);
        }
        catch (HttpRequestException)
        {
            _task = null;
            _logs = [];
        }
        finally
        {
            _loading = false;
        }

        SetBreadcrumb();
        await FollowAsync();
    }

    private void SetBreadcrumb() => Breadcrumb.Set(new BreadcrumbItem(L["Tasks"], "/tasks"), new BreadcrumbItem(TaskLabel));

    /// <summary>Joins the log stream of a task that is not finished; leaves the one of the task left.</summary>
    private async Task FollowAsync()
    {
        if (!_followingLiveTasks)
        {
            LiveTasks.OnChanged += OnLiveTasksChanged;
            LiveTasks.OnTaskCompleted += OnLiveTaskCompleted;
            _followingLiveTasks = true;
        }

        if (_liveTaskId is { } previous && previous != CurrentTaskId)
            await LeaveAsync(previous);
        if (!InFlight || _liveTaskId == CurrentTaskId) return;

        if (_logHub is null)
        {
            _logHub = HubFactory.Create("logs");
            _logHub.On<TaskLogDto>("LogReceived", log => OnLogsReceived([log]));
            _logHub.On<List<TaskLogDto>>("LogsReceived", OnLogsReceived);
            // Group membership is per connection: a reconnect has to join the task's group again.
            _logHub.RejoinOnReconnect(async () =>
            {
                if (_liveTaskId is { } taskId)
                    await _logHub.InvokeAsync("JoinTaskGroup", taskId);
            });
            try
            {
                await _logHub.StartAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                // The page still shows what it read; without the stream the lines come on the next visit.
                Logger.LogWarning(ex, "[TaskDetail] Log hub connection failed");
                await _logHub.DisposeAsync();
                _logHub = null;
                return;
            }
        }

        _liveTaskId = CurrentTaskId;
        try
        {
            await _logHub.InvokeAsync("JoinTaskGroup", CurrentTaskId);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Microsoft.AspNetCore.SignalR.HubException)
        {
            Logger.LogWarning(ex, "[TaskDetail] JoinTaskGroup failed");
        }
    }

    private async Task LeaveAsync(int taskId)
    {
        _liveTaskId = null;
        if (_logHub is null) return;
        try
        {
            await _logHub.InvokeAsync("LeaveTaskGroup", taskId);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Microsoft.AspNetCore.SignalR.HubException)
        {
            Logger.LogWarning(ex, "[TaskDetail] LeaveTaskGroup failed");
        }
    }

    /// <summary>Lines pushed for this task are added once, in the order of their ids.</summary>
    internal void OnLogsReceived(IReadOnlyList<TaskLogDto> logs)
    {
        var known = _logs.Select(log => log.Id).ToHashSet();
        var added = logs.Where(log => log.TaskId == CurrentTaskId && known.Add(log.Id)).ToList();
        if (added.Count == 0) return;
        _logs = [.. _logs, .. added];
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>The agent started the task: the live list turns it to Running.</summary>
    private void OnLiveTasksChanged()
    {
        var live = LiveTasks.Tasks.FirstOrDefault(task => task.Id == CurrentTaskId);
        if (live is null || _task is null || live.Status == _task.Status) return;
        _task = _task with { Status = live.Status, StartedAt = live.StartedAt ?? _task.StartedAt };
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnLiveTaskCompleted(TaskCompletedNotification notification)
    {
        if (notification.TaskId == CurrentTaskId)
            _ = InvokeAsync(ReloadFinishedAsync);
    }

    /// <summary>The task ended: its final state and its complete log replace what was streamed.</summary>
    internal async Task ReloadFinishedAsync()
    {
        var taskId = CurrentTaskId;
        try
        {
            var task = await Api.Pipelines.GetTaskAsync(taskId);
            if (task is null || CurrentTaskId != taskId) return;
            var logs = await Api.Monitoring.GetTaskLogsAsync(taskId);
            if (CurrentTaskId != taskId) return;
            (_task, _logs) = (task, logs);
        }
        catch (HttpRequestException)
        {
            // The streamed lines stay on screen; the header keeps the last status it knew.
            return;
        }

        if (!InFlight) await LeaveAsync(taskId);
        StateHasChanged();
    }

    private async Task CancelTaskAsync()
    {
        if (!CanCancel || _task is null) return;

        var confirmed = await Dialog.Confirm(
            string.Format(L["CancelTaskConfirm"], _task.Id, _task.Name),
            L["CancelTask"],
            new OmniConfirmOptions { Destructive = true, ConfirmIcon = OmniIconName.Stop, OkButtonText = L["Stop"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;

        var task = _task;
        await Veil.RunAsync(async () =>
        {
            var status = await Api.Pipelines.CancelTaskAsync(task.Id);
            if (!status.Success)
            {
                Toast.Error("Error", "CancelTaskFailed");
                return;
            }

            Toast.Success("TaskCancelled", "TaskCancelledDetail", task.Id);
            _task = await Api.Pipelines.GetTaskAsync(task.Id);
        });
    }

    private void OnPermissionsChanged() => InvokeAsync(StateHasChanged);

    private static string FormatDate(DateTime? value) => value?.ToString("g") ?? "-";

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (_followingLiveTasks)
        {
            LiveTasks.OnChanged -= OnLiveTasksChanged;
            LiveTasks.OnTaskCompleted -= OnLiveTaskCompleted;
        }
        if (_logHub is not null)
            await _logHub.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
