// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.SystemLogs;

/// <summary> The system logs page follows the log files live instead of a Refresh button or a
/// polling toggle. A watcher on the log directory marks it changed; at most every <see
/// cref="Throttle"/> the admin hub is told <c>AdminEntityChanged("SystemLog", 0, "Updated")</c> and
/// the page reloads. The throttle bounds the pushes whatever the write rate, including the lines
/// the page's own reload may write. </summary>
public sealed class SystemLogChangeBroadcaster(
    IConfiguration configuration,
    IAdminChangeNotifier notifier,
    TimeProvider timeProvider,
    ILogger<SystemLogChangeBroadcaster> logger) : BackgroundService
{
    internal static readonly TimeSpan Throttle = TimeSpan.FromSeconds(2);

    private int _changed;

    /// <summary>Marks the logs as changed; the next tick pushes once. Also the test seam.</summary>
    internal void MarkChanged() => Interlocked.Exchange(ref _changed, 1);

    /// <summary>Pushes when something changed since the last tick; true when it pushed.</summary>
    internal async Task<bool> FlushAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _changed, 0) == 0) return false;
        await notifier.BroadcastAsync(AdminEntities.SystemLog, 0, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = configuration["Logging:FileLog:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(directory);
        using var watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        watcher.Changed += (_, _) => MarkChanged();
        watcher.Created += (_, _) => MarkChanged();
        watcher.Deleted += (_, _) => MarkChanged();
        watcher.Renamed += (_, _) => MarkChanged();
        watcher.EnableRaisingEvents = true;

        using var timer = new PeriodicTimer(Throttle, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
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
    }
}
