// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class ProjectTasksSection : ProjectPagedSectionBase<ServerTaskDto>
{
    private PaginatedResult<ServerTaskDto>? _result;
    protected override string DefaultSortBy => "CreatedAt";
    protected override bool DefaultSortDescending => true;

    // The header filters are the base's column filters; the Server column lists the server names of
    // the project's tasks.
    private TaskFilterValuesDto _filterValues = new();
    private int? _filterValuesProjectId;
    private Func<string, string>? _statusText;
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<TaskExecutionStatus>(L);

    private async Task LoadFilterValuesAsync(int projectId)
    {
        if (_filterValuesProjectId == projectId) return;
        _filterValuesProjectId = projectId;
        try
        {
            _filterValues = await Api.Projects.GetProjectTaskFilterValuesAsync(projectId);
        }
        catch (HttpRequestException)
        {
            _filterValues = new TaskFilterValuesDto();
        }
    }

    protected override async Task LoadAsync()
    {
        var projectId = ProjectId;
        _loading = true;
        await LoadFilterValuesAsync(projectId);
        PaginatedResult<ServerTaskDto> result;
        try
        {
            result = await ReadPageAsync(projectId, _page, _pageSize, _sortBy, _sortDescending, _columnFilters);
        }
        catch (HttpRequestException) { result = new PaginatedResult<ServerTaskDto>(); }
        if (ProjectId != projectId) return;
        _result = result;
        _loading = false;
    }

    protected override Task<PaginatedResult<ServerTaskDto>> ReadPageAsync(
        int projectId, int page, int pageSize, string sortBy, bool sortDescending, IReadOnlyList<GridFilter> filters) =>
        Api.Projects.GetProjectTasksAsync(projectId, page, pageSize, sortBy, sortDescending, filters: filters);

    private static OmniTone GetFailureBadge(string failureCode) =>
        failureCode == TaskFailureCodes.ToolError ? OmniTone.Warning : OmniTone.Danger;

    private static OmniIconName GetFailureIcon(string failureCode) => failureCode switch
    {
        TaskFailureCodes.InfrastructureMismatch => OmniIconName.Wrench,
        TaskFailureCodes.TestsFailed => OmniIconName.Flask,
        TaskFailureCodes.BuildFailed => OmniIconName.Wrench,
        _ => OmniIconName.Warning
    };

    private static string SourceServerName(ServerTaskDto task) =>
        string.IsNullOrWhiteSpace(task.ServerName) ? $"#{task.ServerId}" : task.ServerName;

    /// <summary>The export writes the source the cell links to.</summary>
    private Func<ServerTaskDto, object?> SourceExport => _sourceExport ??= task => task.PipelineRunId is { } pipelineRunId
        ? $"{L["PipelineRun"]} #{pipelineRunId}"
        : $"{L["Server"]}: {SourceServerName(task)}";
    private Func<ServerTaskDto, object?>? _sourceExport;
}
