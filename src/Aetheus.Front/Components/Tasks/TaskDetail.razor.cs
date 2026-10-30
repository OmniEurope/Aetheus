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

    [Parameter] public int TaskId { get; set; }

    private ServerTaskDto? _task;
    private List<TaskLogDto> _logs = [];
    private bool _loading;
    private int? _loadedTaskId;

    // Subscribe to the task log group while the agent runs and obtain status from the shared live
    // task list.
    private HubConnection? _logHub;
    private int? _liveTaskId;
    private bool _followingLiveTasks;

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

    protected override async Task OnParametersSetAsync()
    {
        Breadcrumb.Set(
            new BreadcrumbItem(L["Tasks"], "/tasks"),
            new BreadcrumbItem($"{L["Task"]} #{TaskId}"));
        if (_loadedTaskId == TaskId) return;

        _loadedTaskId = TaskId;
        _loading = true;
        _task = null;
        _logs = [];
        try
        {
            _task = await Api.Pipelines.GetTaskAsync(TaskId);
            if (_task is not null)
                _logs = await Api.Monitoring.GetTaskLogsAsync(TaskId);
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

        await FollowAsync();
    }

    /// <summary>Joins the log stream of a task that is not finished; leaves the one of the task left.</summary>
    private async Task FollowAsync()
    {
        if (!_followingLiveTasks)
        {
            LiveTasks.OnChanged += OnLiveTasksChanged;
            LiveTasks.OnTaskCompleted += OnLiveTaskCompleted;
            _followingLiveTasks = true;
        }

        if (_liveTaskId is { } previous && previous != TaskId)
            await LeaveAsync(previous);
        if (!InFlight || _liveTaskId == TaskId) return;

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

        _liveTaskId = TaskId;
        try
        {
            await _logHub.InvokeAsync("JoinTaskGroup", TaskId);
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
        var added = logs.Where(log => log.TaskId == TaskId && known.Add(log.Id)).ToList();
        if (added.Count == 0) return;
        _logs = [.. _logs, .. added];
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>The agent started the task: the live list turns it to Running.</summary>
    private void OnLiveTasksChanged()
    {
        var live = LiveTasks.Tasks.FirstOrDefault(task => task.Id == TaskId);
        if (live is null || _task is null || live.Status == _task.Status) return;
        _task = _task with { Status = live.Status, StartedAt = live.StartedAt ?? _task.StartedAt };
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnLiveTaskCompleted(TaskCompletedNotification notification)
    {
        if (notification.TaskId == TaskId)
            _ = InvokeAsync(ReloadFinishedAsync);
    }

    /// <summary>The task ended: its final state and its complete log replace what was streamed.</summary>
    internal async Task ReloadFinishedAsync()
    {
        var taskId = TaskId;
        try
        {
            var task = await Api.Pipelines.GetTaskAsync(taskId);
            if (task is null || TaskId != taskId) return;
            var logs = await Api.Monitoring.GetTaskLogsAsync(taskId);
            if (TaskId != taskId) return;
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
