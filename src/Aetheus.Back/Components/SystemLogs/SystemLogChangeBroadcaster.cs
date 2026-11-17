// SPDX-License-Identifier: EUPL-1.2
using System.Threading.Channels;
using OmniEurope.Diagnostics.Logs;

namespace Aetheus.Back.Components.SystemLogs;

/// <summary> The system logs page follows the log files live instead of a Refresh button or a
/// polling toggle. The OmniEurope.Diagnostics change signal watches the log directory and raises
/// <see cref="ILogChanges.Changed"/> at most every <see cref="ILogChanges.ThrottleMilliseconds"/>,
/// including for the lines the page's own reload may write; each signal tells the admin hub
/// <c>AdminEntityChanged("SystemLog", 0, "Updated")</c> and the page reloads. </summary>
public sealed class SystemLogChangeBroadcaster(
    ILogChanges changes,
    IAdminChangeNotifier notifier,
    ILogger<SystemLogChangeBroadcaster> logger) : BackgroundService
{
    // One pending signal is enough: signals arriving while a push is in flight fold into the next one.
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>Marks the logs as changed; the loop pushes once. Also the test seam.</summary>
    internal void MarkChanged() => _signals.Writer.TryWrite(true);

    /// <summary>Waits for a change, then tells the open pages once.</summary>
    internal async Task PushNextChangeAsync(CancellationToken ct)
    {
        await _signals.Reader.ReadAsync(ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.SystemLog, 0, EntityChangeOps.Updated, ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnChanged(object? sender, EventArgs args) => MarkChanged();
        changes.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PushNextChangeAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Best effort: a failed push only delays the page until the next change.
                    logger.LogDebug(exception, "System log live push failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            changes.Changed -= OnChanged;
        }
    }
}
