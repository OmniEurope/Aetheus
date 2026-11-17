// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>R-41: the grid of a host system's CVE, with the server column when several servers are listed.</summary>
public partial class ServerHostFindingsGrid : ComponentBase
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private GridExportContext ExportContext { get; set; } = default!;

    [Parameter] public IReadOnlyList<ServerHostFindingDto> Items { get; set; } = [];

    /// <summary>True in the CVE watch tab, where the findings of several servers are listed.</summary>
    [Parameter] public bool ShowServer { get; set; }

    /// <summary>The system upgrade simulation of a finding's server (ADR-024, dry-run first); no item when unset.</summary>
    [Parameter] public EventCallback<ServerHostFindingDto> OnDryRun { get; set; }

    /// <summary>Whether the caller holds the system upgrade right on the server (R-11).</summary>
    [Parameter] public bool CanUpgrade { get; set; }

    [Parameter] public RenderFragment? EmptyContent { get; set; }

    private string ExportTitle => ExportContext.Title(L["ServerHostCveTitle"]);

    private Func<ServerHostFindingDto, object?>? _severityExport;
    private Func<ServerHostFindingDto, object?> SeverityExport => _severityExport ??= finding => L.Localize(finding.Severity);

    private Func<ServerHostFindingDto, object?>? _fixExport;
    private Func<ServerHostFindingDto, object?> FixExport => _fixExport ??= finding => finding.FixedVersion ?? L["CveWatchNoFix"].Value;

    private static readonly IReadOnlyList<string> SeverityValues =
        [nameof(AnalysisSeverity.Critical), nameof(AnalysisSeverity.High), nameof(AnalysisSeverity.Medium), nameof(AnalysisSeverity.Low), nameof(AnalysisSeverity.Info)];

    private Func<string, string>? _severityText;
    private Func<string, string> SeverityText => _severityText ??= value =>
        Enum.TryParse<AnalysisSeverity>(value, out var severity) ? L.Localize(severity) : value;

    private Func<string, string>? _yesNoText;
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);

    private Func<ServerHostFindingDto, object?>? _exploitedExport;
    private Func<ServerHostFindingDto, object?> ExploitedExport => _exploitedExport ??= finding => L[finding.ExploitedSince is not null ? "Yes" : "No"].Value;

    /// <summary>The Severity filter keeps the findings whose severity is one of the ticked ones.</summary>
    internal static readonly Func<ServerHostFindingDto, string, bool> SeverityMatches =
        (finding, filter) => OmniDataGridFilterValues.Split(filter).Contains(finding.Severity.ToString());

    /// <summary>The Exploited filter: True keeps the exploited findings, False the others.</summary>
    internal static readonly Func<ServerHostFindingDto, string, bool> ExploitedMatches =
        (finding, filter) => OmniDataGridFilterValues.Split(filter).Contains((finding.ExploitedSince is not null).ToString());

    /// <summary>The Fixed in filter: True keeps the findings a fix exists for, False those with none.</summary>
    internal static readonly Func<ServerHostFindingDto, string, bool> FixMatches =
        (finding, filter) => OmniDataGridFilterValues.Split(filter).Contains(ServerHostFixability.IsFixable(finding).ToString());

    internal static string ServerCvePath(int serverId) => $"servers/{serverId}/services?tab=cve";

    internal static string ServerUpdatesPath(int serverId) => $"servers/{serverId}/services?tab=updates";

    internal static OmniTone SeverityTone(AnalysisSeverity severity) => severity switch
    {
        AnalysisSeverity.Critical => OmniTone.Danger,
        AnalysisSeverity.High => OmniTone.Warning,
        AnalysisSeverity.Medium => OmniTone.Info,
        _ => OmniTone.Neutral
    };
}
