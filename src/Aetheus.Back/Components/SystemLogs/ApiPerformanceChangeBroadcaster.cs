// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Performance;

namespace Aetheus.Back.Components.SystemLogs;

/// <summary> The administration Performance page reloads when new API timings were recorded,
/// instead of waiting for a Refresh click. Every API request is a sample, so the push is coalesced:
/// the first sample after a quiet spell is announced at once, and the samples of the next <see
/// cref="MinimumSpacing"/> are folded into one later announcement. Nothing is sent while no API
/// request arrives. The broadcast rides the admin hub (Admin only), the same audience as the report
/// endpoint; it carries no timing, the page fetches the report through the authorized API.
/// </summary>
public sealed class ApiPerformanceChangeBroadcaster(
    RequestPerformanceRecorder recorder,
    IAdminChangeNotifier notifier,
    TimeProvider timeProvider,
    ILogger<ApiPerformanceChangeBroadcaster> logger) : BackgroundService
{
    /// <summary>Shortest gap between two announcements: an open page reloads at most this often.</summary>
    public static readonly TimeSpan MinimumSpacing = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await AnnounceNextChangeAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(MinimumSpacing, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Waits for recorded samples, then tells the open Performance pages once.</summary>
    internal async Task AnnounceNextChangeAsync(CancellationToken ct)
    {
        await recorder.WaitForChangeAsync(ct).ConfigureAwait(false);
        try
        {
            await notifier.BroadcastAsync(AdminEntities.ApiPerformance, 0, EntityChangeOps.Updated, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory catch: a failed push must not stop the loop; the next sample announces again.
            logger.LogWarning(ex, "API performance change push failed");
        }
    }
}
