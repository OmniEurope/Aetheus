// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Front.Components.Analysis;

/// <summary>R-14: the project overview's summary of the open CVE of the watch, by severity, linking to the CVE section of the quality page.</summary>
public partial class ProjectCveTile : ComponentBase
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private CveWatchProjectDto? _project;
    private int? _loadedProjectId;

    private string SectionHref => $"projects/{ProjectId}/quality?tab=cve";

    /// <summary>The severities that have at least one open CVE, worst first, then the exploited ones. With none open, the tile says
    /// whether that is proved: an inventory that is not complete, or a source out of date, takes the proof away.</summary>
    private string DetailText
    {
        get
        {
            if (_project is null) return string.Empty;
            if (_project.OpenCount == 0)
                return Unproved(_project) ? L["CveWatchNoMatchNoProof"] : L["CveWatchNoMatch"];
            List<string> parts = [];
            AddSeverity(parts, _project.CriticalCount, AnalysisSeverity.Critical);
            AddSeverity(parts, _project.HighCount, AnalysisSeverity.High);
            AddSeverity(parts, _project.MediumCount, AnalysisSeverity.Medium);
            AddSeverity(parts, _project.LowCount, AnalysisSeverity.Low);
            if (_project.ExploitedCount > 0)
                parts.Add(string.Format(L["CveProjectTileExploited"], _project.ExploitedCount));
            return string.Join(" - ", parts);
        }
    }

    private static bool Unproved(CveWatchProjectDto project) =>
        project.Inventories.Count == 0
        || project.Inventories.Exists(inventory => inventory.Coverage != ProjectInventoryCoverage.Complete)
        || project.Sources.Exists(source => source.IsStale);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        try
        {
            _project = await Api.Analysis.GetCveWatchProjectAsync(ProjectId);
        }
        catch (HttpRequestException)
        {
            _project = null;
        }
    }

    private void AddSeverity(List<string> parts, int count, AnalysisSeverity severity)
    {
        if (count > 0) parts.Add($"{count.ToString(CultureInfo.CurrentCulture)} {L.Localize(severity)}");
    }
}
