// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R-7: the quality gate of the servers' host systems, modeled on the project Quality page (R-17). With no
/// <see cref="ServerId"/> it lists every server the user may read (a grade each; a row opens that server's own page); with a
/// <see cref="ServerId"/> (the Quality entry of a server's menu) only that server's detail. The grade is the server's own and never
/// enters a project's grade; a vulnerability without a published fix is shown but not counted in it (<see cref="ServerHostGrade"/>).
/// </summary>
public partial class ServerQualitySection : ComponentBase
{
    /// <summary>The server of the page; null for the general page of all servers.</summary>
    [Parameter] public int? ServerId { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private GridExportContext ExportContext { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private List<ServerQualityDto> _servers = [];
    private ServerHostVulnerabilitiesDto? _findings;
    private int? _loadedServerId;
    private bool _loaded;
    private bool _loadFailed;
    private bool _findingsLoading;
    private int _tabIndex;
    private List<ServerHostFindingDto> _fixableFindings = [];
    private List<ServerHostFindingDto> _withoutFixFindings = [];
    private const int FindingsTabIndex = ServerQualityOverview.FindingsTab;
    private static readonly string[] TabSlugs = ["overview", "findings", "nofix"];

    /// <summary>The server whose detail is shown: the one of the page; the general page lists the servers and leaves the
    /// detail to each server's own page (same content, the server fixed).</summary>
    internal ServerQualityDto? Selected => ServerId is { } id ? _servers.FirstOrDefault(server => server.ServerId == id) : null;

    private void OpenServer(int serverId) => Nav.NavigateTo($"servers/{serverId}/quality");

    /// <summary>Selects a tab and writes it in the address (the tiles of the Overview are shortcuts to the others).</summary>
    private void SelectTab(int index)
    {
        _tabIndex = Math.Clamp(index, 0, TabSlugs.Length - 1);
        Nav.NavigateTo(Nav.GetUriWithQueryParameter("tab", _tabIndex == 0 ? null : TabSlugs[_tabIndex]));
    }

    /// <summary>A tab title with its count; the count stays when it is zero (an empty tab says so).</summary>
    private static string SectionText(string label, int count) => $"{label} · {count:N0}";

    protected override async Task OnParametersSetAsync()
    {
        if (_loaded && _loadedServerId == ServerId) return;
        _loadedServerId = ServerId;
        await LoadAsync();
    }

    internal async Task LoadAsync()
    {
        _loadFailed = false;
        try
        {
            _servers = await Api.Security.GetHostQualityAsync(ServerId);
        }
        catch (HttpRequestException)
        {
            _servers = [];
            _loadFailed = true;
        }

        _loaded = true;
        await LoadFindingsAsync();
    }

    private async Task LoadFindingsAsync()
    {
        _findings = null;
        _fixableFindings = [];
        _withoutFixFindings = [];
        if (ServerId is not { } id || _loadFailed) return;
        _findingsLoading = true;
        try
        {
            _findings = await Api.Security.GetHostVulnerabilitiesAsync(id);
            // The one fixable rule of the grade and of the overview tables (ServerHostFixability) splits the two tabs.
            _fixableFindings = [.. _findings.Findings.Where(ServerHostFixability.IsFixable)];
            _withoutFixFindings = [.. _findings.Findings.Where(finding => !ServerHostFixability.IsFixable(finding))];
        }
        catch (HttpRequestException)
        {
            _loadFailed = true;
        }
        finally
        {
            _findingsLoading = false;
        }
    }

    private AnalysisGrade? WorstGrade => QualityMenuStatus.LowestServerGrade(_servers);

    private string FixableText(ServerQualityDto server) => string.Format(L["ServerQualityFixableOf"], server.FixableCount, server.OpenCount);

    private static string DateText(DateTime? date) => date?.ToLocalTime().ToString("g") ?? string.Empty;

}
