// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Middleware;

/// <summary>Aggregate refused unmatched requests by minute, using Warning at PeakPerMinute and
/// Information below it. Flush on the next unmatched request from a later minute and include the
/// exact covered minute.</summary>
public sealed class UnmatchedRequestCounter(TimeProvider time, ILogger<UnmatchedRequestCounter> logger)
{
    /// <summary>Treat a sustained average of ten refused requests per second over a minute as a
    /// flood.</summary>
    public const int PeakPerMinute = 600;

    private readonly Lock _gate = new();
    private DateTime _minute;
    private int _count;

    public void Record()
    {
        var now = time.GetUtcNow().UtcDateTime;
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        (DateTime Minute, int Count)? closed = null;
        lock (_gate)
        {
            if (minute != _minute)
            {
                if (_count > 0)
                    closed = (_minute, _count);
                _minute = minute;
                _count = 0;
            }
            _count++;
        }

        if (closed is { } done)
            logger.Log(
                done.Count >= PeakPerMinute ? LogLevel.Warning : LogLevel.Information,
                "{Count} refused requests to no route of the application during the minute starting {Minute:O}",
                done.Count,
                done.Minute);
    }
}
