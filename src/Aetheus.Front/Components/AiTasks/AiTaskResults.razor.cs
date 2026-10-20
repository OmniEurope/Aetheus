// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Markdig;

namespace Aetheus.Front.Components.AiTasks;

/// <summary>
/// Recette R3-15 and R3-17: one AI task's runs in a page instead of a dialog. Summary tiles, the runs as a
/// timeline (newest first, one selected), and the selected run's report; the Diff tab only exists when the
/// run produced a patch. A run started here, or by its task, refreshes the page when it finishes.
/// </summary>
public partial class AiTaskResults : IDisposable
{
    private const int PageSize = 20;
    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private TaskTrackerService TaskTracker { get; set; } = default!;

    [Parameter] public int TaskId { get; set; }
    [Parameter] public int? ProjectId { get; set; }
    [SupplyParameterFromQuery(Name = "result")] public int? ResultId { get; set; }

    private AiTaskDefinitionDto? _task;
    private List<AiRunResultDto> _runs = [];
    private AiRunResultDto? _selected;
    private string _reportHtml = string.Empty;
    private int _totalCount;
    private int _page = 1;
    private bool _loading = true;
    private bool _loadFailed;
    private int? _runningServerTaskId;
    private bool _starting;

    private bool CanWrite => _task is not null && (_task.ProjectId is { } projectId
        ? Permissions.CanWrite(ResourceType.Project, projectId)
        : Permissions.CanWrite(ResourceType.Server, _task.ServerId));

    private string ListUrl => ProjectId is { } projectId ? $"/projects/{projectId}/ai-tasks" : "/ai-tasks";

    protected override async Task OnInitializedAsync()
    {
        TaskTracker.OnTaskCompleted += OnTaskCompleted;
        await LoadAsync(ResultId);
        await SetBreadcrumbAsync();
    }

    private async Task SetBreadcrumbAsync()
    {
        var taskItem = new BreadcrumbItem(_task?.Name ?? $"#{TaskId}");
        if (ProjectId is not { } projectId)
        {
            Breadcrumb.Set(new BreadcrumbItem(L["AiTasks"], ListUrl), taskItem);
            return;
        }
        ProjectDetailDto? project;
        try { project = await Api.Projects.GetProjectDetailAsync(projectId); }
        catch (HttpRequestException) { project = null; }
        Breadcrumb.Set(
            new BreadcrumbItem(L["Projects"], "/projects"),
            new BreadcrumbItem(project?.Name ?? $"{L["Project"]} #{projectId}", $"/projects/{projectId}/overview"),
            new BreadcrumbItem(L["AiTasks"], ListUrl),
            taskItem);
    }

    private async Task LoadAsync(int? selectId)
    {
        _loading = true;
        _loadFailed = false;
        try
        {
            _task ??= await Api.Ai.GetAiTaskAsync(TaskId);
            var page = await Api.Ai.GetAiResultsAsync(page: _page, pageSize: PageSize, definitionId: TaskId);
            _runs = page.Items;
            _totalCount = page.TotalCount;
            Select(_runs.FirstOrDefault(run => run.Id == selectId) ?? _runs.FirstOrDefault());
        }
        catch (HttpRequestException)
        {
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Select(AiRunResultDto? run)
    {
        _selected = run;
        _reportHtml = Markdown.ToHtml(run?.ReportMarkdown ?? string.Empty, MarkdownPipeline);
    }

    internal async Task OnPageChangedAsync(int page)
    {
        _page = page;
        await LoadAsync(null);
    }

    private async Task RunAsync()
    {
        _starting = true;
        try
        {
            var started = await Api.Ai.RunAiTaskAsync(TaskId);
            if (started is null) return;
            _runningServerTaskId = started.TaskId;
            Notify.Info("Queued", "AiRunQueued");
        }
        finally
        {
            _starting = false;
        }
    }

    private void OnTaskCompleted(TaskCompletedNotification notification)
    {
        if (notification.TaskId != _runningServerTaskId) return;
        _ = InvokeAsync(async () =>
        {
            _page = 1;
            await LoadAsync(null);
            // The finished run is the newest one: select it explicitly in case the list was reordered.
            Select(_runs.FirstOrDefault(run => run.ServerTaskId == notification.TaskId) ?? _selected);
            _runningServerTaskId = null;
            StateHasChanged();
        });
    }

    private async Task EditAsync()
    {
        var saved = await Dialog.OpenAsync<AiTaskDialog>(
            L["Edit"],
            new Dictionary<string, object?> { ["DefinitionId"] = TaskId, ["FixedProjectId"] = ProjectId },
            new OmniDialogOptions { Width = "780px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
        if (saved is true)
        {
            _task = null;
            await LoadAsync(_selected?.Id);
        }
    }

    private Task DownloadPatchAsync() =>
        _selected?.DiffPatch is { Length: > 0 } patch
            ? Js.InvokeVoidAsync("downloadFile", $"ai-proposed-{_selected.Id}.patch", patch, "text/x-diff").AsTask()
            : Task.CompletedTask;

    private string SuccessRateText => _runs.Count == 0
        ? "-"
        : string.Create(CultureInfo.CurrentCulture, $"{100.0 * _runs.Count(run => run.Succeeded) / _runs.Count:0} %");

    private string AverageDurationText => _runs.Count == 0
        ? "-"
        : FormatDuration((long)_runs.Average(run => run.DurationMs));

    private string LastRunText => _runs.FirstOrDefault() is { } last
        ? last.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "-";

    internal static string FormatDuration(long durationMs)
    {
        var duration = TimeSpan.FromMilliseconds(durationMs);
        return duration.TotalMinutes >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalMinutes} min {duration.Seconds:00} s")
            : string.Create(CultureInfo.CurrentCulture, $"{duration.TotalSeconds:0.#} s");
    }

    private static string RunDate(AiRunResultDto run) =>
        run.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public void Dispose()
    {
        TaskTracker.OnTaskCompleted -= OnTaskCompleted;
        GC.SuppressFinalize(this);
    }
}
