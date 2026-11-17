// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Front.Components.Analysis;

/// <summary>R-14: the CVE section of a project's quality page. The open CVE of the project are read a page at a time through the same
/// list as the watch tab (filtered on the project); the figures, the inventories that feed the watch, the last pass and the freshness
/// of the sources come from the project's own read.</summary>
public partial class ProjectCveWatchPanel : ComponentBase
{
    internal const int PageSize = 25;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private GridExportContext ExportContext { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>One line of the list: a match of this project, with the advisory it belongs to (severity, exploitation, summary).</summary>
    private sealed record CveProjectRow(CveWatchAdvisoryGroupDto Group, CveWatchMatchDto Match);

    private CveWatchProjectDto? _project;
    private List<CveProjectRow> _rows = [];
    private List<CveWatchSourceDto> _staleSources = [];
    private Func<CveProjectRow, object?>? _severityExport;
    private Func<CveProjectRow, object?>? _advisoryExport;
    private Func<CveProjectRow, object?>? _packageExport;
    private Func<CveProjectRow, object?>? _fixExport;
    private int? _loadedProjectId;
    private int _loadVersion;
    private int _total;
    private int _page = 1;
    private bool _busy;
    private bool _loadFailed;

    private List<CveWatchSourceDto> StaleSources => _staleSources;

    /// <summary>True once the read ended well and none is running: the only moment a sentence about what the watch did not find may be drawn.</summary>
    private bool Settled => !_busy && !_loadFailed && _project is not null;

    // No CVE is only good news when everything was read: an inventory that is not complete, none at all, or a source that is out of
    // date, takes that reading away, and the section says so instead of "nothing".
    private bool HasCoverageGap => _project is { } project
        && (project.Inventories.Count == 0
            || project.Inventories.Exists(inventory => inventory.Coverage != ProjectInventoryCoverage.Complete)
            || project.Sources.Exists(source => source.IsStale));

    private string EmptyIcon => HasCoverageGap ? "visibility_off" : "verified_user";
    private string EmptyTitle => HasCoverageGap ? L["CveWatchNoMatchNoProof"] : L["CveWatchNoMatch"];
    private string EmptyDescription => HasCoverageGap ? L["CveWatchNoMatchNoProofHint"] : L["CveWatchNoMatchHint"];

    private Func<CveProjectRow, object?> SeverityExport => _severityExport ??= row => L.Localize(row.Group.Severity);
    private Func<CveProjectRow, object?> AdvisoryExport => _advisoryExport ??= row => row.Group.CveId ?? row.Group.AdvisoryId;
    private Func<CveProjectRow, object?> PackageExport => _packageExport ??= PackageWithOriginText;
    private Func<CveProjectRow, object?> FixExport => _fixExport ??= FixText;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        _page = 1;
        _project = null;
        _rows = [];
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var version = ++_loadVersion;
        _busy = true;
        _loadFailed = false;
        try
        {
            var projectTask = Api.Analysis.GetCveWatchProjectAsync(ProjectId);
            var page = await Api.Analysis.GetCveWatchAdvisoriesAsync(_page, PageSize, projectId: ProjectId);
            var project = await projectTask;
            if (version != _loadVersion) return;
            _project = project ?? new CveWatchProjectDto { ProjectId = ProjectId };
            _staleSources = [.. _project.Sources.Where(source => source.IsStale)];
            _rows = [.. page.Items.SelectMany(group => group.Matches.Where(match => match.ProjectId == ProjectId).Select(match => new CveProjectRow(group, match)))];
            _total = page.TotalCount;
        }
        catch (HttpRequestException)
        {
            if (version != _loadVersion) return;
            _loadFailed = true;
        }
        finally
        {
            if (version == _loadVersion) _busy = false;
        }
    }

    private Task OnPageChangedAsync(int page)
    {
        _page = page;
        return ReloadAsync();
    }

    /// <summary>The row opens the finding the watch wrote for the match, where the recommendation and the upgrade are.</summary>
    private void OpenFinding(CveProjectRow row)
    {
        if (row.Match.FindingId is { } findingId) Navigation.NavigateTo($"/analysis/findings/{findingId}");
    }

    private static string Count(int? value) => value?.ToString(CultureInfo.CurrentCulture) ?? "-";

    private static DateTime AsLocal(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    private static OmniTone SeverityTone(AnalysisSeverity severity) => severity switch
    {
        AnalysisSeverity.Critical => OmniTone.Danger,
        AnalysisSeverity.High => OmniTone.Warning,
        AnalysisSeverity.Medium => OmniTone.Info,
        _ => OmniTone.Neutral
    };

    private static OmniTone CoverageTone(ProjectInventoryCoverage coverage) => coverage switch
    {
        ProjectInventoryCoverage.Complete => OmniTone.Success,
        ProjectInventoryCoverage.Partial => OmniTone.Neutral,
        _ => OmniTone.Warning
    };

    private string PackageText(CveProjectRow row) => $"{row.Match.PackageName} {row.Match.Version}";

    private string PackageWithOriginText(CveProjectRow row) =>
        MatchOriginText(row) is { } origin ? $"{PackageText(row)} ({origin})" : PackageText(row);

    /// <summary>R-14: the inventory a match comes from, its branch or its shortened commit after the origin.</summary>
    private string? MatchOriginText(CveProjectRow row)
    {
        if (row.Match.InventoryOrigin is not { } origin) return null;
        var label = L.Localize(origin);
        return string.IsNullOrWhiteSpace(row.Match.InventoryRef) ? label : $"{label} - {ShortId.Shorten(row.Match.InventoryRef)}";
    }

    private string FixText(CveProjectRow row) => row.Match.FixedVersion ?? L["CveWatchNoFix"];

    private string OriginText(CveWatchProjectInventoryDto inventory)
    {
        var origin = L.Localize(inventory.Origin);
        var detail = string.Join(' ', new[] { inventory.RepositoryName, inventory.Ref }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return detail.Length == 0 ? origin : $"{origin} - {detail}";
    }

    private IReadOnlyList<string> GapLines(CveWatchProjectInventoryDto inventory) => CveInventoryGapText.Describe(inventory.Reason, L);
}
