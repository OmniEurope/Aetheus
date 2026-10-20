// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Where a followed server task stands.</summary>
public enum ServerTaskFollowState
{
    Idle,
    Requested,
    Queued,
    Running,
    Succeeded,
    Failed
}

/// <summary>
/// Recette R3-37: the one way a server page reports the task it launched. The page calls
/// <see cref="Expect"/> as it asks the backend for the task; the follower then binds to the first new
/// task of this server whose name starts with <see cref="NamePrefix"/>, from the live task feed
/// (<see cref="TaskTrackerService"/>, already scoped to the servers the user can read), and shows it
/// waiting, running, done, or failed with the last line of its log as the cause.
/// </summary>
public partial class ServerTaskFollower
{
    [Inject] private TaskTrackerService Tracker { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>The start of the task names this page launches (for instance "RKHunter"); an action may
    /// name a narrower one when it calls <see cref="Expect"/>.</summary>
    [Parameter] public string NamePrefix { get; set; } = string.Empty;

    private string _expectedPrefix = string.Empty;

    private ServerTaskFollowState _state = ServerTaskFollowState.Idle;
    private string _title = string.Empty;
    private int? _taskId;
    private string? _cause;
    private HashSet<int> _known = [];
    private ServerTaskGroup? _group;

    internal ServerTaskFollowState State => _state;
    internal int? TaskId => _taskId;

    protected override void OnInitialized()
    {
        Tracker.OnChanged += OnTrackerChanged;
        Tracker.OnTaskCompleted += OnTaskCompleted;
    }

    /// <summary>Called by the page as it launches a task: <paramref name="title"/> names what it does.</summary>
    public void Expect(string title, string? namePrefix = null)
    {
        _title = title;
        _expectedPrefix = namePrefix ?? NamePrefix;
        _group = null;
        _taskId = null;
        _cause = null;
        _state = ServerTaskFollowState.Requested;
        // The tasks already in flight are not the one this click launches.
        _known = Tracker.Tasks.Select(task => task.Id).ToHashSet();
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>Follows the task the backend just returned, by its id: no name matching needed.</summary>
    public void Follow(string title, int taskId)
    {
        _title = title;
        _group = null;
        _taskId = taskId;
        _cause = null;
        _known = [];
        var running = Tracker.Tasks.Any(task => task.Id == taskId && task.Status == TaskExecutionStatus.Running);
        _state = running ? ServerTaskFollowState.Running : ServerTaskFollowState.Queued;
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>Called by the page as one click launches several tasks: one expected task name start
    /// per task (a Docker project's bulk action, one task per container). Shown as one line until the
    /// last one ends: done when all succeeded, else failed with the first failure's cause.</summary>
    public void ExpectGroup(string title, IEnumerable<string> namePrefixes)
    {
        _title = title;
        _group = new ServerTaskGroup(namePrefixes);
        _taskId = null;
        _cause = null;
        _state = ServerTaskFollowState.Requested;
        _known = Tracker.Tasks.Select(task => task.Id).ToHashSet();
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>One task of the group could not be launched: it is not waited for; when none is left,
    /// nothing is followed.</summary>
    public void Drop(string namePrefix)
    {
        if (_group is null) return;
        _group.Drop(namePrefix);
        if (_group.Count == 0) Forget();
        else _ = InvokeAsync(StateHasChanged);
    }

    internal int? GroupCount => _group?.Count;
    internal int? GroupFinished => _group?.Finished;

    /// <summary>The page could not launch the task at all: nothing to follow.</summary>
    public void Forget()
    {
        _state = ServerTaskFollowState.Idle;
        _group = null;
        _taskId = null;
        _ = InvokeAsync(StateHasChanged);
    }

    private bool Matches(int serverId, string taskName) =>
        serverId == ServerId && taskName.StartsWith(_expectedPrefix, StringComparison.OrdinalIgnoreCase);

    private void OnTrackerChanged()
    {
        if (_state is not (ServerTaskFollowState.Requested or ServerTaskFollowState.Queued or ServerTaskFollowState.Running))
            return;
        if (_group is { } group)
        {
            if (!group.Bind(Tracker.Tasks, ServerId, _known) && !group.AnyRunning(Tracker.Tasks)) return;
            _state = group.AnyRunning(Tracker.Tasks) ? ServerTaskFollowState.Running : ServerTaskFollowState.Queued;
            _ = InvokeAsync(StateHasChanged);
            return;
        }
        var task = _taskId is { } id
            ? Tracker.Tasks.FirstOrDefault(candidate => candidate.Id == id)
            : Tracker.Tasks.FirstOrDefault(candidate =>
                !_known.Contains(candidate.Id) && Matches(candidate.ServerId, candidate.Name));
        if (task is null) return;
        _taskId = task.Id;
        _state = task.Status == TaskExecutionStatus.Running ? ServerTaskFollowState.Running : ServerTaskFollowState.Queued;
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnTaskCompleted(TaskCompletedNotification notification)
    {
        if (_state is ServerTaskFollowState.Idle or ServerTaskFollowState.Succeeded or ServerTaskFollowState.Failed) return;
        if (_group is { } group)
        {
            if (!group.Complete(notification, ServerId, _known)) return;
            _ = group.AllFinished ? InvokeAsync(CompleteGroupAsync) : InvokeAsync(StateHasChanged);
            return;
        }
        var ours = _taskId is { } id
            ? notification.TaskId == id
            // A task so fast it finished before the feed listed it is still the one this page asked for.
            : !_known.Contains(notification.TaskId) && Matches(notification.ServerId, notification.TaskName);
        if (!ours) return;
        _taskId = notification.TaskId;
        _ = InvokeAsync(() => CompleteAsync(notification));
    }

    private async Task CompleteAsync(TaskCompletedNotification notification)
    {
        if (notification.Status == TaskExecutionStatus.Success)
        {
            _state = ServerTaskFollowState.Succeeded;
            _cause = null;
        }
        else
        {
            _state = ServerTaskFollowState.Failed;
            // Recette R3-44: the end of the log, not its last line alone: a service that refused to start
            // ends its task with its status and its configuration check.
            try { _cause = await ServerTaskOutputLoader.TailAsync(Api, notification.TaskId, CauseLines); }
            catch (HttpRequestException) { _cause = null; }
            _cause ??= L["NoLogsAvailable"];
        }
        StateHasChanged();
    }

    private async Task CompleteGroupAsync()
    {
        if (_group?.FirstFailedTaskId is not { } failedId)
        {
            _state = ServerTaskFollowState.Succeeded;
            _cause = null;
        }
        else
        {
            _state = ServerTaskFollowState.Failed;
            _taskId = failedId;
            try { _cause = await ServerTaskOutputLoader.TailAsync(Api, failedId, CauseLines); }
            catch (HttpRequestException) { _cause = null; }
            _cause ??= L["NoLogsAvailable"];
        }
        StateHasChanged();
    }

    /// <summary>How many lines of a failed task's log the follower shows as its cause.</summary>
    internal const int CauseLines = 20;

    private string StatusKey => _state switch
    {
        ServerTaskFollowState.Requested => "ServerTaskRequested",
        ServerTaskFollowState.Queued => "ServerTaskQueued",
        ServerTaskFollowState.Running => "ServerTaskRunning",
        ServerTaskFollowState.Succeeded => "ServerTaskSucceeded",
        _ => "ServerTaskFailed"
    };

    private OmniSeverity Severity => _state switch
    {
        ServerTaskFollowState.Succeeded => OmniSeverity.Success,
        ServerTaskFollowState.Failed => OmniSeverity.Danger,
        _ => OmniSeverity.Info
    };

    private OmniIconName Icon => _state switch
    {
        ServerTaskFollowState.Succeeded => OmniIconName.CheckCircle,
        ServerTaskFollowState.Failed => OmniIconName.Error,
        _ => OmniIconName.Hourglass
    };

    public void Dispose()
    {
        Tracker.OnChanged -= OnTrackerChanged;
        Tracker.OnTaskCompleted -= OnTaskCompleted;
    }
}
