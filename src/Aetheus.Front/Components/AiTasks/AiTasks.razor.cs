// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.AiTasks;

public partial class AiTasks : PagedDataGridPageBase<AiTaskDefinitionDto>, IDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private TaskTrackerService TaskTracker { get; set; } = default!;

    [Parameter] public int? ProjectId { get; set; }

    private List<AiTaskDefinitionDto> _tasks = [];
    private AiConsumptionDto? _consumption;
    private string _search = string.Empty;
    private int? _runningTaskId;

    // The profile names the profile column's checkable filter offers, and the status texts.
    private IReadOnlyList<string> _profileNames = [];
    private Func<string, string>? _enabledText;
    private Func<string, string> EnabledText => _enabledText ??= value =>
        bool.TryParse(value, out var enabled) ? L[enabled ? "Active" : "Disabled"].Value : value;

    protected override void OnInitialized()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        TaskTracker.OnTaskCompleted += OnTaskCompleted;
    }

    // The filter values load after the first render: awaiting them before would let the grid load
    // first and OnParametersSetAsync load it a second time.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        await LoadFilterValuesAsync();
        StateHasChanged();
    }

    /// <summary>The names of the profiles the user can pick, the ones a task in scope can use.</summary>
    private async Task LoadFilterValuesAsync()
    {
        try
        {
            var options = await Api.Ai.GetAiRunnerProfileOptionsAsync();
            _profileNames = [.. options.Select(profile => profile.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (HttpRequestException)
        {
            _profileNames = [];
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (ProjectId is { } projectId)
        {
            ProjectDetailDto? project;
            try { project = await Api.Projects.GetProjectDetailAsync(projectId); }
            catch (HttpRequestException) { project = null; }
            Breadcrumb.Set(
                new BreadcrumbItem(L["Projects"], "/projects"),
                new BreadcrumbItem(project?.Name ?? $"{L["Project"]} #{projectId}", $"/projects/{projectId}/overview"),
                new BreadcrumbItem(L["AiTasks"]));
        }
        else
        {
            Breadcrumb.Set(new BreadcrumbItem(L["AiTasks"]));
        }
        // The grid loads itself when it first renders; a later project change reloads it.
        if (_grid is not null) await ReloadPageAsync();
    }

    private Task ReloadAsync() => ReloadPageAsync();

    internal Task LoadDataAsync(GridLoadArgs args) => LoadPageFromArgsAsync(args);

    /// <summary>One page of the export, with the search, sort and column filters of the
    /// grid.</summary>
    internal Task<PaginatedResult<AiTaskDefinitionDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(25);
        var (sortBy, sortDescending) = args.ToSortRequest("Name");
        return Api.Ai.GetAiTasksAsync(page: page, pageSize: pageSize, search: _search, projectId: ProjectId,
            filters: args.ToApiFilters(), sortBy: sortBy, sortDescending: sortDescending);
    }

    protected override async Task LoadPageAsync(int page, int pageSize)
    {
        var filters = _columnFilters;
        var result = await LoadPageResultAsync(async () =>
        {
            var resultTask = Api.Ai.GetAiTasksAsync(
                page: page, pageSize: pageSize, search: _search, projectId: ProjectId,
                filters: filters, sortBy: _sortBy, sortDescending: _sortDescending);
            // The grid scrolls and fetches further blocks as it goes; the consumption summary does
            // not depend on the block, so only the first one (a load or a reload) reads it.
            if (page > 1) return await resultTask;
            var consumptionTask = Api.Ai.GetAiConsumptionAsync(ProjectId);
            await Task.WhenAll(resultTask, consumptionTask);
            _consumption = consumptionTask.Result;
            return resultTask.Result;
        });
        _tasks = result.Items;
    }

    private Task OnSearchChangedAsync() => ReloadAsync();

    private async Task OpenDialogAsync(AiTaskDefinitionDto? definition)
    {
        var result = await Dialog.OpenAsync<AiTaskDialog>(
            definition is null ? L["Create"] : L["Edit"],
            new Dictionary<string, object?>
            {
                ["DefinitionId"] = definition?.Id,
                ["FixedProjectId"] = ProjectId
            },
            new OmniDialogOptions { Width = "780px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
        if (result is true) await ReloadAsync();
    }

    private async Task RunAsync(AiTaskDefinitionDto definition)
    {
        _runningTaskId = definition.Id;
        try
        {
            var started = await Api.Ai.RunAiTaskAsync(definition.Id);
            if (started is null) return;
            var completion = new TaskCompletionSource<TaskCompletedNotification>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void CompleteMatchingRun(TaskCompletedNotification notification)
            {
                if (notification.TaskId == started.TaskId)
                    completion.TrySetResult(notification);
            }
            TaskTracker.OnTaskCompleted += CompleteMatchingRun;
            try
            {
                var result = await FindRunResultAsync(definition.Id, started.TaskId);
                if (result is null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await completion.Task.WaitAsync(timeout.Token); }
                    catch (OperationCanceledException) { }
                    result = await FindRunResultAsync(definition.Id, started.TaskId);
                }

                if (result is null)
                {
                    Notify.Info("Queued", "AiRunQueued");
                    return;
                }

                // Recette R3-17: the finished run opens on the task's results page, selected.
                Nav.NavigateTo($"{ResultsUrl(definition)}?result={result.Id}");
            }
            finally
            {
                TaskTracker.OnTaskCompleted -= CompleteMatchingRun;
            }
        }
        finally
        {
            _runningTaskId = null;
        }
    }

    private async Task<AiRunResultDto?> FindRunResultAsync(int definitionId, int taskId)
    {
        var results = await Api.Ai.GetAiResultsAsync(definitionId: definitionId);
        return results.Items.FirstOrDefault(item => item.ServerTaskId == taskId);
    }

    // Recette R3-15/R3-17: a task's runs live on their own page, reached from the row or Results.
    private void OpenResults(AiTaskDefinitionDto definition) => Nav.NavigateTo(ResultsUrl(definition));

    private string ResultsUrl(AiTaskDefinitionDto definition) =>
        ProjectId is { } projectId ? $"/projects/{projectId}/ai-tasks/{definition.Id}" : $"/ai-tasks/{definition.Id}";

    private async Task DeleteAsync(AiTaskDefinitionDto definition)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["DeleteAiTaskConfirm"], definition.Name), L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;
        await Veil.RunAsync(() => Ui.RunAsync(
            () => Api.Ai.DeleteAiTaskAsync(definition.Id),
            "Deleted",
            ReloadAsync,
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted"));
    }

    private static string FormatDuration(long durationMs) =>
        DisplayFormatting.Duration(TimeSpan.FromMilliseconds(durationMs));

    private bool CanCreate =>
        Permissions.CanWrite(ResourceType.Project)
        || Permissions.CanWrite(ResourceType.Server)
        || Permissions.GetPermissions().Any(permission =>
            permission.ResourceType is ResourceType.Project or ResourceType.Server
            && permission.Permission >= Permission.Write);

    private bool CanWrite(AiTaskDefinitionDto definition) =>
        definition.ProjectId is { } projectId
            ? Permissions.CanWrite(ResourceType.Project, projectId)
            : Permissions.CanWrite(ResourceType.Server, definition.ServerId);

    private bool CanAdmin(AiTaskDefinitionDto definition) =>
        definition.ProjectId is { } projectId
            ? Permissions.CanAdmin(ResourceType.Project, projectId)
            : Permissions.CanAdmin(ResourceType.Server, definition.ServerId);

    private void OnPermissionsChanged() => InvokeAsync(StateHasChanged);

    private void OnTaskCompleted(TaskCompletedNotification notification)
    {
        // A finished run refreshes the rows in place, page, sort and filters kept.
        if (ShouldReloadFor(notification))
            _ = InvokeAsync(RefreshPageAsync);
    }

    internal static bool ShouldReloadFor(TaskCompletedNotification notification) =>
        notification.Operation == OperationKind.AiRun;

    public void Dispose()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        TaskTracker.OnTaskCompleted -= OnTaskCompleted;
    }

    /// <summary>The export writes the source the cell links to.</summary>
    private Func<AiTaskDefinitionDto, object?> SourceExport => _sourceExport ??= task =>
        task.ProjectId is { } projectId ? $"{L["Project"]}: {task.ProjectName ?? $"#{projectId}"}"
        : task.ServerId is { } serverId ? $"{L["Server"]}: {task.ServerName ?? $"#{serverId}"}"
        : string.Empty;
    private Func<AiTaskDefinitionDto, object?>? _sourceExport;
}
