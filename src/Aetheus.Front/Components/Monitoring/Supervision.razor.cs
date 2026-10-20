// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Monitoring;

/// <summary> The Supervision page. It stays current without a manual reload: the <c>entities</c> hub
/// pushes a Project change on every application status transition and when a project is created or
/// edited (coalesced into one reload), and a periodic reload covers what no push announces (visitors
/// of the last five minutes, the storage of each project) and a hub that is unavailable. </summary>
public partial class Supervision
{
    /// <summary>The repeat reload that covers the figures no push announces.</summary>
    internal static readonly TimeSpan FallbackRefreshInterval = TimeSpan.FromSeconds(30);

    private const int LiveReloadWindowMs = 1000;

    [Inject] private AuthStateProvider Auth { get; set; } = default!;

    private readonly TrailingReloadCoalescer _liveReload = new(LiveReloadWindowMs);
    private readonly CancellationTokenSource _lifetime = new();
    private AppMonitoringSummaryDto? _summary;
    private ProjectStorageReportDto? _storage;
    private bool _loadFailed;
    private bool _storageLoading = true;
    private bool _storageFailed;
    private bool _disposed;

    private static string Number(long value) => value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>"3 up, 1 in alert": how the total of the first tile splits.</summary>
    private string AppsDetail => _summary is null
        ? string.Empty
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, L["SupervisionAppsDetail"],
            _summary.UpCount, _summary.DownCount + _summary.DegradedCount);

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        await FollowEntitiesAsync(ResourceType.Project);
        _ = RefreshPeriodicallyAsync(_lifetime.Token);
    }

    /// <summary>Reads the summary (already scoped to the caller's projects server-side, like the
    /// dashboard's) and the storage of each project; each failure is reported on its own.</summary>
    internal async Task LoadAsync()
    {
        var summaryTask = ReadSummaryAsync();
        var storageTask = ReadStorageAsync();
        await Task.WhenAll(summaryTask, storageTask);
    }

    private async Task ReadSummaryAsync()
    {
        try
        {
            _summary = await Api.Monitoring.GetAppMonitoringSummaryAsync() ?? new AppMonitoringSummaryDto();
            _loadFailed = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // A failed reload keeps the figures on screen; only a first load that fails says so.
            _loadFailed = _summary is null;
        }
    }

    private async Task ReadStorageAsync()
    {
        try
        {
            _storage = await Api.Monitoring.GetProjectStorageAsync();
            _storageFailed = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _storageFailed = _storage is null;
        }
        finally
        {
            _storageLoading = false;
        }
    }

    protected override Task OnEntitiesChangedAsync() => _liveReload.RequestBestEffortAsync(ReloadAsync, _lifetime.Token);

    protected override void OnPermissionsChanged()
    {
        // The page does not follow permission changes (FollowPermissions is never called).
    }

    /// <summary>The reload of a push or of the periodic timer: the rows stay on screen until the new ones land.</summary>
    internal Task ReloadAsync() => InvokeAsync(async () =>
    {
        if (_disposed) return;
        await LoadAsync();
        StateHasChanged();
    });

    private async Task RefreshPeriodicallyAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(FallbackRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was left.
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        await base.DisposeAsync();
        _lifetime.Dispose();
    }
}
