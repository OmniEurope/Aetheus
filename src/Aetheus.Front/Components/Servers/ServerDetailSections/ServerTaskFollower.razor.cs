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
    [Inject] private TimeProvider Clock { get; set; } = default!;

    /// <summary>R-119: the server pages' loader, whose layout veils the section while a followed task runs.
    /// Absent outside a server page: the follower then only reports, as before.</summary>
    [CascadingParameter(Name = "ServerLoader")] public ServerDetailLoader? Loader { get; set; }

    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>R-119: the page shows an inventory the agent collects and sends back (Apache sites, for
    /// instance). When the followed task ends, the veil then stays until the server's next state has arrived,
    /// or <see cref="SettleLimit"/> has passed and the state is read again, so a row that is gone or moved never
    /// shows again as it was.</summary>
    [Parameter] public bool SettleOnHeartbeat { get; set; }

    /// <summary>R-119: raised once the wait of <see cref="SettleOnHeartbeat"/> is over (the server's next state has
    /// arrived, or <see cref="SettleLimit"/> has passed), before the veil lifts. A page whose data is not the
    /// server state itself (the firewall rules, the pending updates, a grid) reads it again here, so the veil
    /// never lifts on what the task has just changed.</summary>
    [Parameter] public EventCallback OnSettled { get; set; }

    /// <summary>How long the veil waits for a launched task to show in the live feed before it gives up
    /// (the follower line stays): a name that matches nothing must not veil the page for ever.</summary>
    internal static readonly TimeSpan UnboundLimit = TimeSpan.FromSeconds(30);

    /// <summary>How long the veil waits, after the task ended, for the server's next state.</summary>
    internal static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(15);

    /// <summary>The start of the task names this page launches (for instance "RKHunter"); an action may
    /// name a narrower one when it calls <see cref="Expect"/>.</summary>
    [Parameter] public string NamePrefix { get; set; } = string.Empty;

    /// <summary>Recette R-12: a task of this server whose name starts with <see cref="NamePrefix"/> and that is already
    /// queued or running when the page opens (or starts while the follower is idle, for instance after the live feed
    /// has just been read) is followed like one the page launched, veil included, so leaving a page and coming back
    /// never hides an action in progress. Pipeline tasks are not adopted (their run page reports them), and a
    /// follower without a <see cref="NamePrefix"/> adopts nothing: it would match every task of the server.</summary>
    [Parameter] public bool AdoptRunning { get; set; } = true;

    /// <summary>Recette R-12: the starts of the task names a page adopts on arrival when they are not all one
    /// <see cref="NamePrefix"/> (the patch page launches "System update check" and "System package upgrade", the
    /// services page "Service", "Install" and "Uninstall"). Absent, <see cref="NamePrefix"/> is the only start
    /// adopted; with neither, nothing is adopted.</summary>
    [Parameter] public IReadOnlyList<string>? AdoptPrefixes { get; set; }

    /// <summary>Recette R-12: the starts of task names that are never adopted on arrival although they match
    /// <see cref="NamePrefix"/> (the Docker page shares its "Docker" prefix with reads of a few seconds, which must not
    /// veil it). A task the page launches itself is still followed through <see cref="Expect"/>.</summary>
    [Parameter] public IReadOnlyList<string>? NotAdoptedPrefixes { get; set; }

    /// <summary>Recette R-12: names, for the user, a task adopted on arrival from its raw backend name (for instance
    /// "Certbot RenewalCheck" becomes "Test à blanc"), as the page names the task it launches itself. Absent, or
    /// returning null or blank, the raw name is shown.</summary>
    [Parameter] public Func<string, string?>? AdoptedTitle { get; set; }

    // Recette R-37: which tasks veil the page (a task adopted on arrival, a task the page launches itself) is decided in
    // one place, ServerBlockingTasks; every other task shows its progress block and nothing is veiled.

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
        TryAdopt();
    }

    // Recette R-12: the global feed (TaskTrackerService) knows every task in flight, whichever page launched it.
    private void TryAdopt()
    {
        if (!AdoptRunning || _state != ServerTaskFollowState.Idle) return;
        var prefixes = AdoptPrefixes ?? (NamePrefix.Length == 0 ? [] : (IReadOnlyList<string>)[NamePrefix]);
        var task = ServerTaskAdoption.FindInFlight(Tracker.Tasks, ServerId, prefixes, NotAdoptedPrefixes);
        if (task is null) return;
        _expectedPrefix = NamePrefix;
        var localized = AdoptedTitle?.Invoke(task.Name);
        _title = string.IsNullOrWhiteSpace(localized) ? task.Name : localized;
        _group = null;
        _taskId = task.Id;
        _cause = null;
        _known = [];
        _state = task.Status == TaskExecutionStatus.Running ? ServerTaskFollowState.Running : ServerTaskFollowState.Queued;
        Launched(veil: ServerBlockingTasks.Veils(task.Name), adopted: true);
    }

    /// <summary>Called by the page as it launches a task: <paramref name="title"/> names what it does.
    /// R-19: a one-off action (disable a site, revoke a certificate, delete...) shows the progress block and veils
    /// nothing; <paramref name="veil"/> is true only for a blocking task (see ServerBlockingTasks), whose section
    /// is then veiled until the task ends (R-119). A task that only reads (a log, a configuration test) passes
    /// <paramref name="settle"/> false: it changed nothing, so on an inventory page the block does not wait for a
    /// new server state after it.</summary>
    public void Expect(string title, string? namePrefix = null, bool veil = false, bool settle = true)
    {
        _title = title;
        _expectedPrefix = namePrefix ?? NamePrefix;
        _group = null;
        _taskId = null;
        _cause = null;
        _state = ServerTaskFollowState.Requested;
        // The tasks already in flight are not the one this click launches.
        _known = Tracker.Tasks.Select(task => task.Id).ToHashSet();
        Launched(veil, settle: settle);
    }

    /// <summary>Follows the task the backend just returned, by its id: no name matching needed.</summary>
    public void Follow(string title, int taskId, bool veil = false, bool settle = true)
    {
        _title = title;
        _group = null;
        _taskId = taskId;
        _cause = null;
        _known = [];
        var running = Tracker.Tasks.Any(task => task.Id == taskId && task.Status == TaskExecutionStatus.Running);
        _state = running ? ServerTaskFollowState.Running : ServerTaskFollowState.Queued;
        Launched(veil, settle, followUnseen: Tracker.Tasks.All(task => task.Id != taskId));
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
        Launched(veil: false);
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
        EndSettle();
        _ = InvokeAsync(() =>
        {
            SyncVeil();
            StateHasChanged();
        });
    }

    // A new launch ends the wait of the previous one and decides, for itself, whether it veils the section.
    private void Launched(bool veil, bool settle = true, bool followUnseen = false, bool adopted = false)
    {
        EndSettle();
        _veil = veil;
        _adopted = adopted;
        _settleWanted = settle;
        _unboundGaveUp = false;
        _followUnseen = followUnseen;
        SyncVeil();
        _ = InvokeAsync(StateHasChanged);
    }

    // ---- R-119: the section veil -------------------------------------------------------------------------

    private enum VeilTimer { None, Unbound, Settle }

    private bool _veil = true;
    private bool _settleWanted = true;
    private bool _unboundGaveUp;
    private bool _followUnseen;
    private bool _settling;
    private DateTime _settleAfter;
    private IDisposable? _hold;
    private ITimer? _timer;
    private VeilTimer _timerKind;

    internal bool Veiling => _hold is not null;

    /// <summary>Recette R-12: true while a task adopted on arrival runs (until it has ended and its settle wait is over).
    /// The page, which did not launch it and so holds no "launching" state of its own, disables its actions on it: a
    /// second launch is impossible meanwhile. A task the page launched is covered by the page's own state. It does not
    /// depend on a loader being present.</summary>
    public bool Busy => _adopted && !_unboundGaveUp && (_settling || InFlight);

    private bool _adopted;

    /// <summary>Raised when <see cref="Busy"/> changes, so the owning page re-renders its actions.</summary>
    public event Action? BusyChanged;

    private bool _lastBusy;

    private void NotifyBusy()
    {
        var busy = Busy;
        if (busy == _lastBusy) return;
        _lastBusy = busy;
        BusyChanged?.Invoke();
    }

    private bool InFlight => _state is ServerTaskFollowState.Requested or ServerTaskFollowState.Queued or ServerTaskFollowState.Running;

    /// <summary>The veil is held from the launch until the task has ended and, when the page asked for it,
    /// the server's next state has arrived.</summary>
    private void SyncVeil()
    {
        NotifyBusy();
        if (Loader is null) return;
        // R-19: the task is followed (its timers run) whether or not the section is veiled; only a global
        // operation holds the veil.
        var active = !_unboundGaveUp && (_settling || InFlight);
        var wanted = _veil && active;
        if (wanted) _hold ??= Loader.HoldTaskVeil();
        else
        {
            _hold?.Dispose();
            _hold = null;
        }

        var kind = !active ? VeilTimer.None : _settling ? VeilTimer.Settle : _state == ServerTaskFollowState.Requested || _followUnseen ? VeilTimer.Unbound : VeilTimer.None;
        if (kind == _timerKind) return;
        _timer?.Dispose();
        _timerKind = kind;
        _timer = kind == VeilTimer.None
            ? null
            : Clock.CreateTimer(_ => _ = InvokeAsync(() => OnVeilTimer(kind)), null,
                kind == VeilTimer.Settle ? SettleLimit : UnboundLimit, Timeout.InfiniteTimeSpan);
    }

    private async Task OnVeilTimer(VeilTimer kind)
    {
        if (kind != _timerKind) return;
        if (kind == VeilTimer.Unbound && (_state == ServerTaskFollowState.Requested || _followUnseen))
        {
            _unboundGaveUp = true;
            SyncVeil();
            StateHasChanged();
        }
        else if (kind == VeilTimer.Settle)
        {
            await EndSettleAsync(refresh: true);
        }
    }

    private void BeginSettle()
    {
        if (!SettleOnHeartbeat || !_settleWanted || Loader is null) return;
        _settleAfter = Loader.LastUpdated ?? DateTime.MinValue;
        if (!_settling) Loader.OnChanged += OnLoaderChanged;
        _settling = true;
    }

    private void EndSettle()
    {
        if (!_settling) return;
        _settling = false;
        if (Loader is not null) Loader.OnChanged -= OnLoaderChanged;
    }

    private void OnLoaderChanged()
    {
        if (_settling && (Loader?.LastUpdated ?? DateTime.MinValue) > _settleAfter)
            _ = InvokeAsync(() => EndSettleAsync(refresh: false));
    }

    private bool _settleEnding;

    private async Task EndSettleAsync(bool refresh)
    {
        // One end per wait: a beat and the timer may both come while the page reads its data.
        if (!_settling || _settleEnding) return;
        _settleEnding = true;
        try
        {
            if (refresh && Loader is not null)
            {
                // The heartbeat did not come in time: read the state the backend holds.
                try { await Loader.RefreshAsync(ServerId); }
                catch (HttpRequestException) { /* the next heartbeat brings it */ }
            }
            // The page reads its own data now, still under the veil.
            await OnSettled.InvokeAsync();
        }
        finally
        {
            _settleEnding = false;
            EndSettle();
            SyncVeil();
            StateHasChanged();
        }
    }

    private bool Matches(int serverId, string taskName) =>
        serverId == ServerId && taskName.StartsWith(_expectedPrefix, StringComparison.OrdinalIgnoreCase);

    private void OnTrackerChanged()
    {
        if (_state == ServerTaskFollowState.Idle)
        {
            TryAdopt();
            return;
        }
        if (_state is not (ServerTaskFollowState.Requested or ServerTaskFollowState.Queued or ServerTaskFollowState.Running))
            return;
        if (_group is { } group)
        {
            if (!group.Bind(Tracker.Tasks, ServerId, _known) && !group.AnyRunning(Tracker.Tasks)) return;
            _state = group.AnyRunning(Tracker.Tasks) ? ServerTaskFollowState.Running : ServerTaskFollowState.Queued;
            _ = InvokeAsync(RenderWithVeil);
            return;
        }
        var task = _taskId is { } id
            ? Tracker.Tasks.FirstOrDefault(candidate => candidate.Id == id)
            : Tracker.Tasks.FirstOrDefault(candidate =>
                !_known.Contains(candidate.Id) && Matches(candidate.ServerId, candidate.Name));
        if (task is null)
        {
            // R-19: a task of the zone other than the followed one came or went: the block's count may have changed.
            _ = InvokeAsync(StateHasChanged);
            return;
        }
        _taskId = task.Id;
        _followUnseen = false;
        _state = task.Status == TaskExecutionStatus.Running ? ServerTaskFollowState.Running : ServerTaskFollowState.Queued;
        _ = InvokeAsync(RenderWithVeil);
    }

    private void RenderWithVeil()
    {
        SyncVeil();
        StateHasChanged();
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
        if (!ours)
        {
            _ = InvokeAsync(StateHasChanged);
            return;
        }
        _taskId = notification.TaskId;
        _ = InvokeAsync(() => CompleteAsync(notification));
    }

    // ---- R-19: one progress block per zone, whatever the number of tasks ------------------------------------

    /// <summary>True while the block shows work in progress: a task requested, waiting or running, or one that
    /// succeeded and whose new server state has not arrived yet (nothing is veiled meanwhile unless the task is a global
    /// operation). A failure is shown at once, with its cause.</summary>
    internal bool Progressing => !_unboundGaveUp && (InFlight || (_settling && _state == ServerTaskFollowState.Succeeded));

    /// <summary>How many tasks the block stands for: the followed one plus every other task of this server in flight
    /// that this page launches (same name starts). One, the block names its task; more, it says how many are running.</summary>
    internal int RunningCount => !Progressing ? 0 : _group is not null ? 1 : 1 + OtherInFlight(_taskId).Count();

    private IEnumerable<ServerTaskDto> OtherInFlight(int? exceptTaskId)
    {
        var starts = new List<string>(AdoptPrefixes ?? []);
        if (NamePrefix.Length > 0) starts.Add(NamePrefix);
        if (_expectedPrefix.Length > 0) starts.Add(_expectedPrefix);
        return Tracker.Tasks.Where(task => task.ServerId == ServerId
            && task.PipelineRunId is null
            && task.Id != exceptTaskId
            && ServerTaskAdoption.IsInFlight(task)
            && starts.Any(start => start.Length > 0 && task.Name.StartsWith(start, StringComparison.OrdinalIgnoreCase))
            && !(NotAdoptedPrefixes ?? []).Any(excluded => task.Name.StartsWith(excluded, StringComparison.OrdinalIgnoreCase)));
    }

    // The followed task succeeded while another one of the zone still runs: the block goes on with that one, it does not
    // announce "done" over work in progress.
    private bool FollowAnotherInFlight(int endedTaskId)
    {
        if (_group is not null) return false;
        var other = OtherInFlight(endedTaskId).FirstOrDefault();
        if (other is null) return false;
        var localized = AdoptedTitle?.Invoke(other.Name);
        _title = string.IsNullOrWhiteSpace(localized) ? other.Name : localized;
        _taskId = other.Id;
        _cause = null;
        _state = other.Status == TaskExecutionStatus.Running ? ServerTaskFollowState.Running : ServerTaskFollowState.Queued;
        return true;
    }

    private async Task CompleteAsync(TaskCompletedNotification notification)
    {
        if (notification.Status == TaskExecutionStatus.Success && FollowAnotherInFlight(notification.TaskId))
        {
            StateHasChanged();
            return;
        }
        if (notification.Status == TaskExecutionStatus.Success)
        {
            _state = ServerTaskFollowState.Succeeded;
            _cause = null;
            EndOfTask();
        }
        else
        {
            _state = ServerTaskFollowState.Failed;
            // The task ended: the veil does not wait for the log to be read (a failure changes the server too).
            EndOfTask();
            // Recette R3-44: the end of the log, not its last line alone: a service that refused to start
            // ends its task with its status and its configuration check.
            try { _cause = await ServerTaskOutputLoader.TailAsync(Api, notification.TaskId, CauseLines); }
            catch (HttpRequestException) { _cause = null; }
            _cause ??= L["NoLogsAvailable"];
        }
        StateHasChanged();
    }

    private void EndOfTask()
    {
        _followUnseen = false;
        BeginSettle();
        SyncVeil();
    }

    private async Task CompleteGroupAsync()
    {
        if (_group?.FirstFailedTaskId is not { } failedId)
        {
            _state = ServerTaskFollowState.Succeeded;
            _cause = null;
            EndOfTask();
        }
        else
        {
            _state = ServerTaskFollowState.Failed;
            _taskId = failedId;
            EndOfTask();
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
        // R-119: leaving the page never leaves its section veiled.
        EndSettle();
        _timer?.Dispose();
        _timer = null;
        _hold?.Dispose();
        _hold = null;
    }
}
