// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Shared;

public partial class TelemetrySendIntervalNote
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int AppId { get; set; }

    /// <summary>The chart reads the hourly rollup (windows beyond 24 hours): one point per hour, not per send.</summary>
    [Parameter] public bool HourlyPoints { get; set; }

    [Parameter] public string? Class { get; set; }

    private AppTelemetrySendIntervalDto? _interval;
    private int _lastAppId = -1;

    private string SettingText => _interval is null
        ? string.Empty
        : TelemetrySendIntervalText.Setting(L, _interval.ServerIntervalSeconds, _interval.Profile, _interval.IsProjectOverride);

    private string? ObservationText => _interval is null ? null : TelemetrySendIntervalText.Observation(L, _interval);

    protected override async Task OnParametersSetAsync()
    {
        if (AppId == _lastAppId) return;
        _lastAppId = AppId;
        try
        {
            _interval = await Api.Monitoring.GetAppSendIntervalAsync(AppId);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // A note, not the view: without it the telemetry still reads, so it is simply not shown.
            _interval = null;
        }
    }
}
