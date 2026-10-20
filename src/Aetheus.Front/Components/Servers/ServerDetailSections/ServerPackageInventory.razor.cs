// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R3-59: queues the agent's read-only list of the installed packages and shows it in a grid with no
/// action. A list whose closing <c>TOTAL</c> line is missing or does not match the lines received is said as
/// incomplete with the end of its log, never shown as the full list. Reopened while a read runs, the card
/// finds it again in the live task feed. Windows servers have no such list: the card is not shown there.
/// </summary>
public partial class ServerPackageInventory : IDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private TaskTrackerService Tracker { get; set; } = default!;
    [Inject] private ILogger<ServerPackageInventory> Logger { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;
    [Parameter, EditorRequired] public int ServerId { get; set; }

    private bool _queuing;
    private int? _taskId;
    private bool _running;
    private InstalledPackagesReportDto? _report;
    private string? _failure;

    internal bool Busy => _queuing || _running;
    internal InstalledPackagesReportDto? Report => _report;

    private bool AgentCanList => Server.AgentCapabilities.Contains(AgentCapabilities.PackageInventory, StringComparer.Ordinal);
    private bool AgentOffline => AgentOfflineGuard.IsOffline(Server);
    private bool IsWindows => OsTypeHelper.FromDescription(Server.OsDescription) == OsType.Windows;

    protected override void OnInitialized()
    {
        AdoptOpenRead();
        Tracker.OnChanged += OnTrackerChanged;
        Tracker.OnTaskCompleted += OnTaskCompleted;
    }

    private void OnTrackerChanged()
    {
        if (AdoptOpenRead()) _ = InvokeAsync(StateHasChanged);
    }

    private bool AdoptOpenRead()
    {
        if (Busy) return false;
        var open = Tracker.Tasks.FirstOrDefault(task => task.ServerId == ServerId
            && task.Status is TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned or TaskExecutionStatus.Running
            && task.Name == InstalledPackages.TaskName);
        if (open is null) return false;
        _taskId = open.Id;
        _running = true;
        return true;
    }

    private async Task ListAsync()
    {
        if (Busy || !AgentCanList || AgentOfflineGuard.Blocks(Server, Toast)) return;
        _queuing = true;
        _failure = null;
        try
        {
            var taskId = await Api.Auth.ListInstalledPackagesAsync(ServerId);
            if (taskId is null)
            {
                Toast.Error(L["ActionFailed"]);
                return;
            }
            _taskId = taskId;
            _running = true;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Installed package list could not be queued for server {ServerId}", ServerId);
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _queuing = false;
        }
    }

    private void OnTaskCompleted(TaskCompletedNotification notification)
    {
        if (_running && notification.TaskId == _taskId)
            _ = InvokeAsync(() => ShowResultAsync(notification.TaskId, notification.Status));
    }

    private async Task ShowResultAsync(int taskId, TaskExecutionStatus status)
    {
        var output = await ServerTaskOutputLoader.ReadOrEmptyAsync(Api, taskId);
        var report = InstalledPackages.Parse(output);
        var shown = status == TaskExecutionStatus.Success && report.IsComplete;
        _running = false;
        _report = shown ? report : null;
        _failure = shown ? null : FailureCause(output, status);
        StateHasChanged();
    }

    // The end of the log without the package lines themselves: the agent's error, or the notice of lines
    // dropped on the way, which is why the list is incomplete.
    private string FailureCause(string output, TaskExecutionStatus status)
    {
        var cause = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith(InstalledPackages.PackagePrefix + "\t", StringComparison.Ordinal))
            .TakeLast(ServerTaskFollower.CauseLines)
            .ToList();
        return cause.Count > 0 ? string.Join('\n', cause) : L.Localize(status);
    }

    public void Dispose()
    {
        Tracker.OnChanged -= OnTrackerChanged;
        Tracker.OnTaskCompleted -= OnTaskCompleted;
    }
}
