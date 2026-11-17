// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// R-41: the CVE of the server's host system, read with the server's Read right. From a finding, the system upgrade is
/// offered the way ADR-024 orders it: a simulation first (the dry run of the packages and updates tab, which needs the
/// system upgrade right), then the apply from that tab.
/// </summary>
public partial class ServerHostCveSection : ComponentBase
{
    [Parameter, EditorRequired] public int ServerId { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    // Recette R-12: the dry run started from a finding is shown again when the user comes back to the tab.
    private static readonly string[] DryRunTaskPrefixes = [ServerTaskNames.PatchCheck];

    private ServerTaskFollower? _follower;

    internal string? AdoptedTitle(string taskName) => ServerAdoptedTaskTitles.Resolve(taskName,
        [ServerAdoptedTaskTitles.Key(L, ServerTaskNames.PatchCheck, "PatchCheck")]);
    private ServerHostVulnerabilitiesDto? _data;
    private bool _loading = true;
    private bool _loadFailed;
    private bool _busy;
    private int? _loadedServerId;

    // The hosting page re-renders on every heartbeat: only another server reads the findings again.
    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId == ServerId) return;
        _loadedServerId = ServerId;
        await LoadAsync();
    }

    internal async Task LoadAsync()
    {
        _loading = true;
        _loadFailed = false;
        try
        {
            _data = await Api.Security.GetHostVulnerabilitiesAsync(ServerId);
        }
        catch (HttpRequestException)
        {
            _data = null;
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private IReadOnlyList<ServerHostFindingDto> Findings => _data?.Findings ?? [];

    private int OpenCount => Findings.Count(finding => finding.Status == VulnerabilityMatchStatus.Affected);

    private int ExploitedCount => Findings.Count(finding => finding.ExploitedSince is not null);

    private int FixableCount => Findings.Count(finding => finding.FixedVersion is not null);

    private string ReportText => _data?.ReportedAt is { } reported
        ? string.Format(L["ServerHostCveReported"], _data.OsName, _data.PackageCount, reported.ToLocalTime().ToString("g"))
        : string.Empty;

    // R-11: the upgrade, simulated or not, is the named permission server.system.upgrade.
    private bool CanUpgrade => Permissions.Can(PermissionKeys.ServerSystemUpgrade, ServerId);

    private async Task DryRunAsync(ServerHostFindingDto finding)
    {
        if (!CanUpgrade || _busy) return;
        _busy = true;
        try
        {
            var taskId = await Api.Security.UpgradeSystemAsync(finding.ServerId, dryRun: true);
            if (taskId is { } queued)
            {
                _follower?.Follow(L["PatchCheck"], queued);
                Toast.Success("ServerHostCveDryRunQueued", "ServerHostCveDryRunQueued");
            }
            else
            {
                Toast.Error("PatchActionFailed", "PatchActionFailed");
            }
        }
        catch (HttpRequestException)
        {
            Toast.Error("PatchActionFailed", "PatchActionFailed");
        }
        finally
        {
            _busy = false;
        }
    }
}
