// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class ProjectLogsSection : ProjectPagedSectionBase<TaskLogDto>
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    private PaginatedResult<TaskLogDto>? _result;
    protected override string DefaultSortBy => "Timestamp";
    protected override bool DefaultSortDescending => true;

    // The Level header filter is a checkable list of every log level.
    private Func<string, string>? _levelText;
    private Func<string, string> LevelText => _levelText ??= GridFilterText.ForEnum<TaskLogLevel>(L);

    protected override async Task LoadAsync()
    {
        var projectId = ProjectId;
        _loading = true;
        PaginatedResult<TaskLogDto> result;
        try
        {
            result = await ReadPageAsync(projectId, _page, _pageSize, _sortBy, _sortDescending, _columnFilters);
        }
        catch (HttpRequestException) { result = new PaginatedResult<TaskLogDto>(); }
        if (ProjectId != projectId) return;
        _result = result;
        _loading = false;
    }

    protected override Task<PaginatedResult<TaskLogDto>> ReadPageAsync(
        int projectId, int page, int pageSize, string sortBy, bool sortDescending, IReadOnlyList<GridFilter> filters) =>
        Api.Projects.GetProjectLogsAsync(projectId, page, pageSize, sortBy, sortDescending, filters: filters);

    // Recette R3-35: clicking a line opens that exact entry.
    private Task OpenEntryAsync(TaskLogDto log) => Logs.LogEntryDialog.OpenAsync(Dialog, L, log.Message);

    private static OmniTone GetLevelBadge(TaskLogLevel level) => DisplayFormatting.TaskLogBadge(level);
}
