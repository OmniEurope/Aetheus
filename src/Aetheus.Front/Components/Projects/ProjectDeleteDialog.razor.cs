// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Projects;

/// <summary> The confirmation of a project deletion. It shows what the deletion removes, read from
/// the server's preview, refuses while a run of the project is in progress, and enables Delete only
/// once the project's name is typed exactly. Recette R-97 (ADR-058): it also asks the agents of the
/// project's servers what the project deployed there; Delete removes those resources first, then the project.
/// Listed Docker volumes (the project's data) need their own acknowledgement.
/// Recette R-49: the most useful counts stay in sight, the rest (other counts, server resources) behind
/// "Afficher plus". Recette R-50: the whole deletion runs under one veil, set once and cleared once, with nothing
/// moving under it; it ends in the same dialog on what was deleted and what was not, with a single Fermer that
/// goes back to the projects list. Recette R-51: when something stays on a server, the project is not half deleted
/// but waits, "Suppression à terminer", for the cleanup to be relaunched from this same dialog (the name is then
/// not asked again). </summary>
public partial class ProjectDeleteDialog : IDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan s_maxWait = TimeSpan.FromMinutes(5);

    /// <summary>The counts shown without "Afficher plus" (recette R-49): what a user looks for first.</summary>
    private static readonly ProjectDeletionItemKind[] s_mainKinds = [ProjectDeletionItemKind.GitRepositories, ProjectDeletionItemKind.Pipelines];

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private GridExportContext ExportContext { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    private readonly CancellationTokenSource _closing = new();
    private ProjectDeletionPreviewDto? _preview;
    private bool _loading = true;
    private bool _deleting;
    private string _confirmation = string.Empty;
    private List<ProjectServerResourceTaskDto> _discoveries = [];
    private List<ProjectServerResourceTaskDto> _removals = [];
    private bool _discovering;
    private bool _discoveryRefused;
    private bool _cleanupRefused;
    private bool _volumesAcknowledged;
    private ProjectDeletionReport? _report;

    /// <summary>Opens the dialog; true once the deletion ran (the project is deleted, or waits for its cleanup).</summary>
    internal static async Task<bool> OpenAsync(OmniDialogService dialog, IStringLocalizer<AppStrings> localizer, int projectId)
    {
        var result = await dialog.OpenAsync<ProjectDeleteDialog>(
            localizer["ProjectDeletionTitle"].Value,
            new Dictionary<string, object?> { [nameof(ProjectId)] = projectId },
            new OmniDialogOptions { Width = "640px", AutoFocusFirstElement = false });
        return result is true;
    }

    /// <summary>The project is already deleted and waits for its server cleanup (recette R-51).</summary>
    private bool Resuming => _preview?.DeletionRequestedAt is not null;

    private bool Busy => _deleting || _discovering
        || _discoveries.Any(IsRunning) || _removals.Any(IsRunning);

    private IEnumerable<ProjectDeletionItemDto> MainItems => _preview?.Items.Where(item => s_mainKinds.Contains(item.Kind)) ?? [];

    private IEnumerable<ProjectDeletionItemDto> OtherItems => _preview?.Items.Where(item => !s_mainKinds.Contains(item.Kind)) ?? [];

    private bool HasFoundResources => _discoveries.Any(task => task.Resources.Any(item => item.Outcome == ProjectServerResourceOutcome.Found));

    /// <summary>Volumes are listed and not yet removed: their data loss must be acknowledged.</summary>
    private bool NeedsVolumeAcknowledgement => _discoveries.Any(task =>
        task.Resources.Any(item => item.Kind == ProjectServerResourceKind.Volume && item.Outcome == ProjectServerResourceOutcome.Found));

    /// <summary>Library values refused for another reason than an environment without a server (recette R-49: an
    /// environment without a server has nothing Aetheus could remove, so it is not mentioned).</summary>
    private bool HasRefusedValues => _preview?.RefusedValues.Any(item => item.Reason != ProjectDeletionRefusal.NoServer) == true;

    private bool CanDelete => _preview is { ActiveRunCount: 0 } preview
        && _report is null
        && !Busy
        && (Resuming || string.Equals(_confirmation.Trim(), preview.ProjectName, StringComparison.Ordinal))
        && (!NeedsVolumeAcknowledgement || _volumesAcknowledged);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _preview = await Api.Projects.GetDeletionPreviewAsync(ProjectId);
        }
        catch (HttpRequestException)
        {
            _preview = null;
        }
        _loading = false;
        if (_preview is not null && _preview.Servers.Any(server => server.State == ProjectDeletionServerState.Ready))
            await DiscoverAsync();
    }

    private async Task DiscoverAsync()
    {
        _discovering = true;
        StateHasChanged();
        List<ProjectServerResourceTaskDto>? queued;
        try
        {
            queued = await Api.Projects.StartDeletionDiscoveryAsync(ProjectId, _closing.Token);
        }
        catch (HttpRequestException)
        {
            queued = null;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The dialog was closed while the request ran: nothing is left to show.
            return;
        }
        _discovering = false;
        if (queued is null)
        {
            _discoveryRefused = true;
            return;
        }
        _discoveries = queued;
        if (queued.Count > 0)
            Toast.Info("ProjectDeletionOnServers", "ProjectDeletionDiscoveryStarted", queued.Count);
        _discoveries = await FollowAsync(_discoveries);
    }

    /// <summary>Recette R-50: one veil from the click to the end, whatever happens on the servers. The cleanup never
    /// stops the deletion: what it could not remove keeps the project waiting for its cleanup (recette R-51), and
    /// the end of the dialog says it.</summary>
    private async Task DeleteAsync()
    {
        if (!CanDelete || _preview is null) return;
        _deleting = true;
        StateHasChanged();
        try
        {
            if (HasFoundResources)
                await CleanServersAsync();
            var taskIds = _discoveries.Concat(_removals).Select(task => task.TaskId).ToList();
            var outcome = await Api.Projects.DeleteProjectAsync(ProjectId, taskIds, _closing.Token);
            if (outcome is { } ended)
            {
                _report = ProjectDeletionReport.Build(ended, _preview, _discoveries, _removals, L, _cleanupRefused);
                if (ended == ProjectDeletionOutcome.Deleted) Toast.Success("Deleted", "Deleted");
                else Toast.Warning("ProjectDeletionPending", "ProjectDeletionPendingIntro");
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The dialog was closed while the deletion ran: nothing is left to show.
        }
        finally
        {
            _deleting = false;
            StateHasChanged();
        }
    }

    /// <summary>Removes what the discoveries found and follows the removals to their end. A refused cleanup (a
    /// run in progress) queues nothing: what was found then stays, and the deletion says so.</summary>
    private async Task CleanServersAsync()
    {
        List<ProjectServerResourceTaskDto>? queued;
        try
        {
            queued = await Api.Projects.StartDeletionCleanupAsync(ProjectId, new ProjectServerCleanupRequest
            {
                DiscoveryTaskIds = [.. _discoveries.Select(task => task.TaskId)],
                AcknowledgeVolumeDataLoss = _volumesAcknowledged,
            }, _closing.Token);
        }
        catch (HttpRequestException)
        {
            queued = null;
        }
        if (queued is null)
        {
            _cleanupRefused = true;
            return;
        }
        _removals = queued;
        if (queued.Count > 0)
            Toast.Info("ProjectDeletionOnServers", "ProjectDeletionRemovalStarted", queued.Count);
        _removals = await FollowAsync(_removals);
    }

    /// <summary>Reads the tasks again until each has finished; one that does not answer in time is said as such,
    /// never as done. Under the veil of a deletion the dialog is not redrawn at each read (recette R-50): the end
    /// says what happened.</summary>
    private async Task<List<ProjectServerResourceTaskDto>> FollowAsync(List<ProjectServerResourceTaskDto> tasks)
    {
        var deadline = Clock.GetUtcNow() + s_maxWait;
        var ids = tasks.Select(task => task.TaskId).ToList();
        while (tasks.Any(IsRunning))
        {
            if (Clock.GetUtcNow() >= deadline)
                return [.. tasks.Select(task => IsRunning(task)
                    ? task with { State = ProjectServerTaskState.Failed, Failure = L["ProjectDeletionNoAnswer"] }
                    : task)];
            try
            {
                await Task.Delay(s_pollInterval, Clock, _closing.Token);
                var fresh = await Api.Projects.GetDeletionServerTasksAsync(ProjectId, ids, _closing.Token);
                if (fresh is not null)
                    tasks = [.. tasks.Select(task => fresh.FirstOrDefault(item => item.TaskId == task.TaskId) ?? task)];
            }
            catch (HttpRequestException)
            {
                // A missed read is retried on the next turn; the deadline still bounds the wait.
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested)
            {
                return tasks;
            }
            if (!_deleting) await InvokeAsync(StateHasChanged);
        }
        return tasks;
    }

    /// <summary>Recette R-50: the single way out once the deletion ran, back to the projects list.</summary>
    private void Close()
    {
        Dialog.Close(true);
        Nav.NavigateTo("/projects");
    }

    private static bool IsRunning(ProjectServerResourceTaskDto task) => task.State == ProjectServerTaskState.Running;

    /// <summary>What to show for a server before the deletion: its discovery.</summary>
    private ProjectServerResourceTaskDto? TaskOf(int serverId) => _discoveries.LastOrDefault(task => task.ServerId == serverId);

    internal static IEnumerable<(string? Environment, IEnumerable<IGrouping<ProjectServerResourceKind, ProjectServerResourceDto>> Kinds)> Groups(
        ProjectServerResourceTaskDto task) =>
        task.Resources
            .GroupBy(item => item.Environment)
            .OrderBy(group => group.Key is null)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, group.GroupBy(item => item.Kind).OrderBy(kinds => kinds.Key).AsEnumerable()));

    private string StateText(ProjectDeletionServerState state) => ProjectDeletionReport.StateText(state, L);

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
        GC.SuppressFinalize(this);
    }
}
