// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R3-23 / R3-26: queues the agent's read of what a mail stack left on the host and shows the
/// answer: clean, or each leftover grouped by kind. A check that did not finish (no closing <c>CLEAN</c>
/// line) is said as such with the end of its log, never as a clean host. Reopened while a check runs, the
/// card finds it again in the live task feed.
/// </summary>
public partial class MailLeftoversCheck : IDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private TaskTrackerService Tracker { get; set; } = default!;
    [Inject] private ILogger<MailLeftoversCheck> Logger { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;
    [Parameter, EditorRequired] public int ServerId { get; set; }

    private static readonly string[] s_kindOrder =
        [MailLeftovers.KindPackage, MailLeftovers.KindPath, MailLeftovers.KindMarker, MailLeftovers.KindPort];

    private bool _queuing;
    private int? _taskId;
    private bool _running;
    private MailLeftoversReportDto? _report;
    private string? _failure;

    internal bool Busy => _queuing || _running;
    internal MailLeftoversReportDto? Report => _report;
    internal int? TaskId => _taskId;

    private bool AgentCanCheck => Server.AgentCapabilities.Contains(AgentCapabilities.MailLeftoversCheck, StringComparer.Ordinal);
    private bool AgentOffline => AgentOfflineGuard.IsOffline(Server);

    protected override void OnInitialized()
    {
        AdoptOpenCheck();
        Tracker.OnChanged += OnTrackerChanged;
        Tracker.OnTaskCompleted += OnTaskCompleted;
    }

    private void OnTrackerChanged()
    {
        if (AdoptOpenCheck()) _ = InvokeAsync(StateHasChanged);
    }

    private bool AdoptOpenCheck()
    {
        if (Busy) return false;
        var open = Tracker.Tasks.FirstOrDefault(task => task.ServerId == ServerId
            && task.Status is TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned or TaskExecutionStatus.Running
            && task.Name == MailTaskNames.LeftoversCheck);
        if (open is null) return false;
        _taskId = open.Id;
        _running = true;
        return true;
    }

    private async Task CheckAsync()
    {
        if (Busy || !AgentCanCheck || AgentOfflineGuard.Blocks(Server, Toast)) return;
        _queuing = true;
        _report = null;
        _failure = null;
        try
        {
            var queued = await Api.Mail.CheckMailLeftoversAsync(ServerId);
            if (queued is null)
            {
                Toast.Error(L["ActionFailed"]);
                return;
            }
            _taskId = queued.TaskId;
            _running = true;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail leftovers check could not be queued for server {ServerId}", ServerId);
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _queuing = false;
        }
    }

    private void OnTaskCompleted(TaskCompletedNotification notification)
    {
        if (!_running || notification.TaskId != _taskId) return;
        _ = InvokeAsync(() => CompleteAsync(notification));
    }

    private async Task CompleteAsync(TaskCompletedNotification notification)
    {
        string output;
        try { output = await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId); }
        catch (HttpRequestException) { output = string.Empty; }
        var report = MailTaskOutputParser.ParseLeftovers(output);
        _running = false;
        if (notification.Status == TaskExecutionStatus.Success && report.IsComplete)
        {
            _report = report;
            _failure = null;
        }
        else
        {
            _report = null;
            var tail = string.Join('\n', output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(ServerTaskFollower.CauseLines));
            _failure = tail.Length > 0 ? tail : L.Localize(notification.Status);
        }
        StateHasChanged();
    }

    internal static IEnumerable<(string Kind, List<MailLeftoverDto> Items)> Groups(MailLeftoversReportDto report) =>
        report.Items
            .GroupBy(item => item.Kind)
            .OrderBy(group => Array.IndexOf(s_kindOrder, group.Key) is var index and >= 0 ? index : s_kindOrder.Length)
            .Select(group => (group.Key, group.ToList()));

    private static string GroupTitleKey(string kind) => kind switch
    {
        MailLeftovers.KindPackage => "MailLeftoversPackages",
        MailLeftovers.KindPath => "MailLeftoversPaths",
        MailLeftovers.KindMarker => "MailLeftoversMarker",
        MailLeftovers.KindPort => "MailLeftoversPorts",
        _ => "MailLeftoversOther"
    };

    private string ItemText(MailLeftoverDto item) => item.Kind switch
    {
        MailLeftovers.KindPackage when item.Detail.StartsWith("rc", StringComparison.Ordinal) =>
            string.Format(L["MailLeftoverPackageConfig"], item.Name),
        MailLeftovers.KindPackage => string.Format(L["MailLeftoverPackageInstalled"], item.Name, item.Detail.Trim()),
        MailLeftovers.KindPort when item.Detail.Length > 0 => string.Format(L["MailLeftoverPortHeld"], item.Name, item.Detail),
        _ => item.Name
    };

    public void Dispose()
    {
        Tracker.OnChanged -= OnTrackerChanged;
        Tracker.OnTaskCompleted -= OnTaskCompleted;
    }
}
