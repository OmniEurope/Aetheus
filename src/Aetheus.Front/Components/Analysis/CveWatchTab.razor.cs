// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Front.Components.Analysis;

/// <summary>R-15: the CVE watch tab of the quality page. The tiles count the open CVE of the projects the caller may read and
/// filter the list when pressed; the list is cut by the server, one entry per CVE with its projects.</summary>
public partial class CveWatchTab : ComponentBase
{
    internal const int PageSize = 25;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private GridExportContext ExportContext { get; set; } = default!;

    /// <summary>The projects of the quality page, which feed the organization and project filters.</summary>
    [Parameter] public IReadOnlyList<AnalysisPortfolioProjectDto> Projects { get; set; } = [];

    private long? _pickedMatchId;
    private Func<CveWatchAdvisoryGroupDto, object?>? _severityExport;
    private Func<CveWatchAdvisoryGroupDto, object?>? _packagesExport;
    private Func<CveWatchAdvisoryGroupDto, object?>? _projectsExport;
    private Func<CveWatchAdvisoryGroupDto, object?>? _fixExport;
    private IReadOnlyList<OmniOption<AnalysisSeverity?>> _severityOptions = [];
    private CveWatchOverviewDto? _overview;
    private List<CveWatchAdvisoryGroupDto> _groups = [];
    private IReadOnlyList<object> _expandedKeys = [];
    private int _total;
    private int _page = 1;
    private bool _busy;
    private bool _loadFailed;
    private int _loadVersion;

    private AnalysisSeverity? _severity;
    private int? _organizationId;
    private int? _projectId;
    private bool _exploitedOnly;
    private bool _withoutFixOnly;
    private bool _includeClosed;

    // The filter options are built once per list of projects (and per chosen organization), not on every render: a new list
    // handed to a dropdown at each pass is a changed parameter, and the tab renders twice for each read.
    private IReadOnlyList<AnalysisPortfolioProjectDto>? _organizationOptionsSource;
    private IReadOnlyList<OmniOption<int?>> _organizationOptions = [];
    private IReadOnlyList<AnalysisPortfolioProjectDto>? _projectOptionsSource;
    private int? _projectOptionsOrganization;
    private IReadOnlyList<OmniOption<int?>> _projectOptions = [];
    private List<CveWatchSourceDto> _staleSources = [];

    private List<CveWatchSourceDto> StaleSources => _staleSources;

    /// <summary>True once both reads ended well and none is running: the only moment a sentence about what the watch did not
    /// find (no CVE) may be drawn. Before it, or after a failure, the page is silent about it.</summary>
    private bool Settled => !_busy && !_loadFailed && _overview is not null;

    private IReadOnlyList<OmniOption<int?>> OrganizationOptions
    {
        get
        {
            if (!ReferenceEquals(_organizationOptionsSource, Projects))
            {
                _organizationOptions =
                [
                    .. Projects.GroupBy(project => project.OrganizationId)
                        .Select(group => new OmniOption<int?>(group.Key, group.First().OrganizationName))
                        .OrderBy(option => option.Text, StringComparer.CurrentCultureIgnoreCase)
                ];
                _organizationOptionsSource = Projects;
            }

            return _organizationOptions;
        }
    }

    private IReadOnlyList<OmniOption<int?>> ProjectOptions
    {
        get
        {
            if (!ReferenceEquals(_projectOptionsSource, Projects) || _projectOptionsOrganization != _organizationId)
            {
                _projectOptions =
                [
                    .. Projects.Where(project => _organizationId is null || project.OrganizationId == _organizationId)
                        .Select(project => new OmniOption<int?>(project.ProjectId, project.ProjectName))
                        .OrderBy(option => option.Text, StringComparer.CurrentCultureIgnoreCase)
                ];
                _projectOptionsSource = Projects;
                _projectOptionsOrganization = _organizationId;
            }

            return _projectOptions;
        }
    }

    private string EmptyIcon => AnyFilter ? "filter_alt_off" : "verified_user";

    private bool AnyFilter => _severity is not null || _organizationId is not null || _projectId is not null
        || _exploitedOnly || _withoutFixOnly || _includeClosed;

    // No CVE is only good news when everything was read: a project with no complete inventory, or a source that is
    // out of date, takes that reading away, and the page says so instead of "nothing".
    private string EmptyTitle => AnyFilter ? L["CveWatchNoMatchFilters"]
        : HasCoverageGap ? L["CveWatchNoMatchNoProof"] : L["CveWatchNoMatch"];

    private string? EmptyDescription => AnyFilter ? null
        : HasCoverageGap ? L["CveWatchNoMatchNoProofHint"].Value : L["CveWatchNoMatchHint"].Value;

    private bool HasCoverageGap => _overview is { } overview && (overview.UncoveredProjects.Count > 0 || overview.Sources.Exists(source => source.IsStale));

    protected override async Task OnInitializedAsync()
    {
        _severityOptions =
        [
            .. new[] { AnalysisSeverity.Critical, AnalysisSeverity.High, AnalysisSeverity.Medium, AnalysisSeverity.Low }
                .Select(severity => new OmniOption<AnalysisSeverity?>(severity, L.Localize(severity)))
        ];
        await LoadAsync(reloadOverview: true);
    }

    private Task ReloadAsync() => LoadAsync(reloadOverview: true);

    private async Task LoadAsync(bool reloadOverview)
    {
        var version = ++_loadVersion;
        _busy = true;
        _loadFailed = false;
        StateHasChanged();
        try
        {
            var overviewTask = reloadOverview ? Api.Analysis.GetCveWatchOverviewAsync() : Task.FromResult(_overview!);
            var page = await Api.Analysis.GetCveWatchAdvisoriesAsync(
                _page, PageSize, _severity, _exploitedOnly, _withoutFixOnly, _includeClosed, _projectId, _organizationId);
            var overview = await overviewTask;
            if (version != _loadVersion) return;
            _overview = overview;
            _staleSources = [.. overview.Sources.Where(source => source.IsStale)];
            _groups = page.Items;
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

    private Task FilterAsync()
    {
        _page = 1;
        _expandedKeys = [];
        return LoadAsync(reloadOverview: false);
    }

    private Task OnOrganizationChangedAsync()
    {
        // A project of another organization cannot stay chosen.
        if (_projectId is { } project && _organizationId is not null
            && !Projects.Any(candidate => candidate.ProjectId == project && candidate.OrganizationId == _organizationId))
            _projectId = null;
        return FilterAsync();
    }

    private Task ToggleSeverityAsync(AnalysisSeverity severity)
    {
        _severity = _severity == severity ? null : severity;
        return FilterAsync();
    }

    private Task ToggleExploitedAsync()
    {
        _exploitedOnly = !_exploitedOnly;
        return FilterAsync();
    }

    private Task ToggleWithoutFixAsync()
    {
        _withoutFixOnly = !_withoutFixOnly;
        return FilterAsync();
    }

    private Task OnPageChangedAsync(int page)
    {
        _page = page;
        _expandedKeys = [];
        return LoadAsync(reloadOverview: false);
    }

    /// <summary>Only one CVE is opened at a time (ExpandMode Single), so one picked line is enough: a line of another CVE
    /// is not found in this one and the first line stands.</summary>
    private void PickMatch(long? matchId) => _pickedMatchId = matchId;

    /// <summary>The project line the timeline follows: the one picked, else the first.</summary>
    private CveWatchMatchDto SelectedMatch(CveWatchAdvisoryGroupDto group) =>
        group.Matches.Find(match => match.MatchId == _pickedMatchId) ?? group.Matches[0];

    private Func<CveWatchAdvisoryGroupDto, object?> SeverityExport => _severityExport ??= group => L.Localize(group.Severity);

    private Func<CveWatchAdvisoryGroupDto, object?> PackagesExport => _packagesExport ??= PackagesText;

    private Func<CveWatchAdvisoryGroupDto, object?> ProjectsExport => _projectsExport ??= ProjectsText;

    private Func<CveWatchAdvisoryGroupDto, object?> FixExport => _fixExport ??= FixText;

    private static string Count(int? value) => value?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "-";

    private static string PressedText(bool pressed) => pressed ? "true" : "false";

    private static DateTimeOffset AsDate(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static OmniTone SeverityTone(AnalysisSeverity severity) => severity switch
    {
        AnalysisSeverity.Critical => OmniTone.Danger,
        AnalysisSeverity.High => OmniTone.Warning,
        AnalysisSeverity.Medium => OmniTone.Info,
        _ => OmniTone.Neutral
    };

    private static OmniTone MatchTone(CveWatchMatchDto match) => match.ClosedAt is not null ? OmniTone.Success
        : match.Status == VulnerabilityMatchStatus.ToVerify ? OmniTone.Neutral : OmniTone.Danger;

    private string MatchText(CveWatchMatchDto match) => match.ClosedAt is not null ? L["CveWatchClosedBadge"] : L.Localize(match.Status);

    private string PackagesText(CveWatchAdvisoryGroupDto group)
    {
        var packages = group.Matches.Select(match => match.PackageName).Distinct(StringComparer.Ordinal).ToList();
        return packages.Count == 1 ? packages[0] : $"{packages[0]} (+{packages.Count - 1})";
    }

    private string ProjectsText(CveWatchAdvisoryGroupDto group)
    {
        var count = group.Matches.Select(match => match.ProjectId).Distinct().Count();
        return string.Format(L[count == 1 ? "CveWatchProjectCountOne" : "CveWatchProjectCountMany"], count);
    }

    /// <summary>The version the advisory is fixed in, when every project agrees on one; "no fix" when none has it.</summary>
    private string FixText(CveWatchAdvisoryGroupDto group)
    {
        var fixes = group.Matches.Select(match => match.FixedVersion).Distinct(StringComparer.Ordinal).ToList();
        if (fixes.Count == 1) return fixes[0] ?? L["CveWatchNoFix"];
        return fixes.All(fix => fix is null) ? L["CveWatchNoFix"] : L["CveWatchFixVaries"];
    }
}
