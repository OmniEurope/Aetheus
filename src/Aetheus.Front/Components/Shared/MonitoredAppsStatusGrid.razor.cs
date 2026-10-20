// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class MonitoredAppsStatusGrid
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary>Null while the summary is loading.</summary>
    [Parameter] public IReadOnlyList<MonitoredAppStatusDto>? Applications { get; set; }
    [Parameter] public bool IsLoading { get; set; }


    private OmniDataGrid<MonitoredAppStatusDto>? _grid;
    private IReadOnlyList<MonitoredAppStatusDto>? _shownApplications;
    private static readonly TimeSpan NewRowHighlight = TimeSpan.FromSeconds(5);

    // The status filter names the states as the badges do; built once so the column sees the same
    // delegate on every render.
    private Func<string, string>? _statusText;
    private Func<string, string> StatusText => _statusText ??= value =>
        Enum.TryParse<AppHealthStatus>(value, ignoreCase: true, out var status) ? L[$"AppHealth{status}"].Value : value;

    /// <summary>Compare live lists before rendering so newly appearing applications can be
    /// highlighted briefly; do not mark rows on the initial load.</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (!ReferenceEquals(Applications, _shownApplications))
        {
            if (_shownApplications is not null && Applications is not null && _grid is not null)
                await _grid.RefreshAsync();
            _shownApplications = Applications;
        }
    }

    /// <summary>The project's supervision page, opened on this application.</summary>
    private static string AppHref(MonitoredAppStatusDto app) => $"/projects/{app.ProjectId}/monitoring?app={app.Id}";

    internal static OmniTone HealthBadge(AppHealthStatus status) => status switch
    {
        AppHealthStatus.Up => OmniTone.Success,
        AppHealthStatus.Down => OmniTone.Danger,
        AppHealthStatus.Degraded => OmniTone.Warning,
        _ => OmniTone.Neutral
    };
}
