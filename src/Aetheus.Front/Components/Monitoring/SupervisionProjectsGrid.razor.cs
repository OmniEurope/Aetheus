// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Monitoring;

/// <summary> The Supervision grid: every project the caller can read, its applications with their
/// status and audience, and its storage against its budget. Over 80 % the share turns orange, at the
/// budget red: the budget sweep then purges the oldest detail. </summary>
public partial class SupervisionProjectsGrid
{

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary>Null while the summary is loading.</summary>
    [Parameter] public IReadOnlyList<MonitoredAppStatusDto>? Applications { get; set; }

    /// <summary>Null while loading, or when the storage could not be read (<see cref="StorageFailed"/>).</summary>
    [Parameter] public ProjectStorageReportDto? Storage { get; set; }
    [Parameter] public bool StorageFailed { get; set; }
    [Parameter] public bool IsLoading { get; set; }
    [Parameter] public bool ShowReclaimLink { get; set; }

    /// <summary>The last-event column shows local time, so its range filter and sort read the same local instant.</summary>
    private static readonly Func<SupervisionRow, object?> LocalLastEventAt = row => row.LastEventAt?.ToLocalTime();

    private IReadOnlyList<OmniDataGridGroup> _groups = [new(nameof(SupervisionRow.ProjectName), false)];
    private IReadOnlyList<SupervisionRow> _rows = [];
    private IReadOnlyList<MonitoredAppStatusDto>? _builtFromApplications;
    private ProjectStorageReportDto? _builtFromStorage;
    private Func<string, string>? _statusText;

    private IReadOnlyList<SupervisionRow> Rows => _rows;

    private Func<string, string> StatusText => _statusText ??= key =>
        key == SupervisionRow.Unmanaged ? L["ProjectUnmanaged"].Value
        : Enum.TryParse<AppHealthStatus>(key, ignoreCase: true, out var status) ? L[$"AppHealth{status}"].Value : key;

    protected override void OnParametersSet()
    {
        // Rebuilt only when a new summary or storage report arrives, so a render keeps the same rows.
        if (ReferenceEquals(_builtFromApplications, Applications) && ReferenceEquals(_builtFromStorage, Storage)) return;
        _builtFromApplications = Applications;
        _builtFromStorage = Storage;
        _rows = SupervisionRow.Build(Applications, Storage);
    }

    private string GroupLabel(object? value, int count) => $"{value} ({count})";

    private static string Megabytes(long bytes) => DisplayFormatting.Size(bytes);

    internal static string Percent(int percent) => string.Create(CultureInfo.CurrentCulture, $"{percent} %");

    internal static OmniTone ShareTone(int percent) =>
        percent >= 100 ? OmniTone.Danger : percent >= ProjectStorageDefaults.AlertPercent ? OmniTone.Warning : OmniTone.Neutral;

    private static OmniTone StatusTone(SupervisionRow row) => row.App is null
        ? OmniTone.Neutral
        : MonitoredAppsStatusGrid.HealthBadge(row.App.CurrentStatus);
}
