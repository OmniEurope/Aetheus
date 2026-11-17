// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

/// <summary>R-22: the page of one run of the CVE watch, the target of the link of the Scan column (R-21).</summary>
public partial class CveWatchRunPage : ComponentBase
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private GridExportContext ExportContext { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;

    [Parameter] public int Id { get; set; }

    private Func<CveWatchRunFindingDto, object?>? _severityExport;
    private Func<CveWatchRunFindingDto, object?>? _packagesExport;
    private Func<CveWatchRunFindingDto, object?>? _statusExport;
    private CveWatchRunDto? _run;
    private bool _loading = true;
    private int _loadedId;

    /// <summary>Where the back button leads: the Quality page of the run's project, else the CVE watch tab.</summary>
    private string BackHref => _run is { ProjectId: > 0 } run ? $"/projects/{run.ProjectId}/quality" : "/analysis?tab=cve-watch";

    private string Title => string.Format(L["CveWatchRunTitle"], CveWatchRunNumber.Format(Id));

    private string Subtitle => _run is null ? string.Empty
        : string.Format(L["CveWatchRunSubtitle"], _run.ProjectName, _run.WrittenAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedId == Id) return;
        _loadedId = Id;
        _loading = true;
        try
        {
            _run = await Api.Analysis.GetCveWatchRunAsync(Id);
        }
        catch (HttpRequestException)
        {
            _run = null;
        }

        if (_run is { ProjectId: > 0 } run)
        {
            // R-21: a run belongs to one project: the page stays in it (trail, menu, back arrow).
            ProjectNav.Set(run.ProjectId);
            Breadcrumb.SetProjectResource(
                run.ProjectId, run.ProjectName, [],
                L["Projects"], L["Project"], L["AnalysisPortfolio"],
                "quality", "/analysis", new BreadcrumbItem(Title));
        }
        else if (_run is not null)
            Breadcrumb.Set(
                new BreadcrumbItem(L["AnalysisPortfolio"], "/analysis"),
                new BreadcrumbItem(L["CveWatchTab"], "/analysis?tab=cve-watch"),
                new BreadcrumbItem(Title));
        _loading = false;
    }

    private Func<CveWatchRunFindingDto, object?> SeverityExport => _severityExport ??= finding => L.Localize(finding.Severity);

    private Func<CveWatchRunFindingDto, object?> PackagesExport => _packagesExport ??= finding => string.Join(", ", finding.Packages);

    private Func<CveWatchRunFindingDto, object?> StatusExport => _statusExport ??= finding => L.Localize(finding.Status);

    private static OmniTone SeverityTone(AnalysisSeverity severity) => severity switch
    {
        AnalysisSeverity.Critical => OmniTone.Danger,
        AnalysisSeverity.High => OmniTone.Warning,
        AnalysisSeverity.Medium => OmniTone.Info,
        _ => OmniTone.Neutral
    };
}
