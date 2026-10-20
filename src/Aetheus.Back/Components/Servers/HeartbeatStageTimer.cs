// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Servers;

/// <summary>Log per-stage durations only when a heartbeat exceeds <see cref="SlowThreshold"/>. Use
/// Information below <see cref="WarningThreshold"/> and Warning above it to distinguish slower
/// processing from actionable delays.</summary>
internal sealed class HeartbeatStageTimer(TimeProvider time)
{
    public static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan WarningThreshold = TimeSpan.FromSeconds(10);

    private readonly long _start = time.GetTimestamp();
    private readonly List<(string Stage, TimeSpan Duration)> _stages = [];
    private long? _last;

    /// <summary>Closes the stage that ran since the previous mark (or since the start).</summary>
    public void Mark(string stage)
    {
        var now = time.GetTimestamp();
        _stages.Add((stage, time.GetElapsedTime(_last ?? _start, now)));
        _last = now;
    }

    public TimeSpan Total => time.GetElapsedTime(_start);

    public IReadOnlyList<(string Stage, TimeSpan Duration)> Stages => _stages;

    public void LogIfSlow(ILogger logger, int serverId)
    {
        var total = Total;
        if (total < SlowThreshold) return;
        logger.Log(
            total >= WarningThreshold ? LogLevel.Warning : LogLevel.Information,
            "Heartbeat of server {ServerId} took {TotalMs} ms: {Stages}",
            serverId,
            Math.Round(total.TotalMilliseconds),
            string.Join(", ", _stages.Select(stage => string.Create(
                CultureInfo.InvariantCulture, $"{stage.Stage} {Math.Round(stage.Duration.TotalMilliseconds)} ms"))));
    }
}
