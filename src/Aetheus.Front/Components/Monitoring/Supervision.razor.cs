// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Monitoring;

public partial class Supervision
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;

    private AppMonitoringSummaryDto? _summary;
    private bool _loadFailed;

    private static string Number(long value) => value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>"3 up, 1 in alert": how the total of the first tile splits.</summary>
    private string AppsDetail => _summary is null
        ? string.Empty
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, L["SupervisionAppsDetail"],
            _summary.UpCount, _summary.DownCount + _summary.DegradedCount);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            // The same summary as the dashboard, which already scopes the apps to the caller's
            // projects server-side.
            _summary = await Api.Monitoring.GetAppMonitoringSummaryAsync() ?? new AppMonitoringSummaryDto();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _loadFailed = true;
        }
    }
}
