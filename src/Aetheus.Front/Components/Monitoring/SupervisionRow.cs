// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Monitoring;

/// <summary> One line of the Supervision grid: a supervised application of a project, or the single
/// "not monitored" line of a project that supervises none. The project's storage rides on every line
/// of the project (the row detail reads it) but is shown only where <see cref="ShowsStorage"/> is
/// set, so a project with several applications never counts its budget twice. </summary>
internal sealed record SupervisionRow(
    int ProjectId,
    string ProjectName,
    MonitoredAppStatusDto? App,
    ProjectStorageUsageDto? Usage,
    bool ShowsStorage)
{
    /// <summary>The status key the status column filters on; "Unmanaged" when no application is supervised.</summary>
    public const string Unmanaged = "Unmanaged";

    public string Key => App is null ? $"project-{ProjectId}" : $"app-{App.Id}";

    public string ApplicationName => App?.Name ?? string.Empty;

    public string StatusKey => App is null ? Unmanaged : App.CurrentStatus.ToString();

    public int? OnlineVisitors => App?.OnlineVisitorCount;

    public DateTime? LastEventAt => App?.LastEventAt;

    public long? UsedBytes => ShowsStorage ? Usage?.UsedBytes : null;

    public long? BudgetBytes => ShowsStorage ? Usage?.BudgetBytes : null;

    public int? UsedPercent => ShowsStorage ? Usage?.UsedPercent : null;

    /// <summary>An application opens its project's supervision page on itself; a project with none opens that page.</summary>
    public string Href => App is null
        ? $"/projects/{ProjectId}/monitoring"
        : $"/projects/{ProjectId}/monitoring?app={App.Id}";

    /// <summary> One row per supervised application, grouped by project. Every project the caller can
    /// read appears (those of the storage report and those owning an application), a project without
    /// application as one "not monitored" row. </summary>
    public static IReadOnlyList<SupervisionRow> Build(
        IReadOnlyList<MonitoredAppStatusDto>? applications,
        ProjectStorageReportDto? storage)
    {
        var usageByProject = (storage?.Projects ?? []).ToDictionary(usage => usage.ProjectId);
        var appsByProject = (applications ?? []).GroupBy(app => app.ProjectId).ToDictionary(group => group.Key, group => group.ToList());
        var rows = new List<SupervisionRow>();
        foreach (var projectId in usageByProject.Keys.Union(appsByProject.Keys)
                     .OrderBy(id => NameOf(id, usageByProject, appsByProject), StringComparer.CurrentCultureIgnoreCase))
        {
            usageByProject.TryGetValue(projectId, out var usage);
            var name = NameOf(projectId, usageByProject, appsByProject);
            if (!appsByProject.TryGetValue(projectId, out var apps))
            {
                rows.Add(new SupervisionRow(projectId, name, null, usage, true));
                continue;
            }

            rows.AddRange(apps
                .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select((app, index) => new SupervisionRow(projectId, name, app, usage, index == 0)));
        }

        return rows;
    }

    private static string NameOf(
        int projectId,
        Dictionary<int, ProjectStorageUsageDto> usageByProject,
        Dictionary<int, List<MonitoredAppStatusDto>> appsByProject) =>
        usageByProject.TryGetValue(projectId, out var usage) ? usage.ProjectName
        : appsByProject[projectId].Select(app => app.ProjectName).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? $"#{projectId}";
}
