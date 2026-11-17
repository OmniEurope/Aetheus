// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The daily visitors of one monitored app with their first visit time and entry page. It loads on its own
/// so a failed read never hides the summary tiles and chart, and it reloads when
/// <see cref="RefreshToken"/> changes (the parent bumps it on a telemetry push).
/// </summary>
public partial class AppWebAnalyticsVisitorsList : ComponentBase
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int AppId { get; set; }

    /// <summary>Changes when the parent learned that new telemetry was stored.</summary>
    [Parameter] public int RefreshToken { get; set; }

    private IReadOnlyList<AppWebAnalyticsVisitorDto> _visitors = [];
    private bool _loading = true;
    private bool _loadFailed;
    private int _loadedAppId = -1;
    private int _loadedToken = -1;

    protected override async Task OnParametersSetAsync()
    {
        if (AppId == _loadedAppId && RefreshToken == _loadedToken)
            return;
        var appChanged = AppId != _loadedAppId;
        _loadedAppId = AppId;
        _loadedToken = RefreshToken;
        await LoadAsync(showBusy: appChanged);
    }

    private Task RetryAsync() => LoadAsync(showBusy: true);

    private async Task LoadAsync(bool showBusy)
    {
        var appId = AppId;
        _loading = showBusy;
        _loadFailed = false;
        try
        {
            var visitors = await Api.Monitoring.GetAppWebAnalyticsVisitorsAsync(appId);
            if (appId == AppId)
                _visitors = visitors ?? [];
        }
        catch (HttpRequestException)
        {
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }
}
