// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Middleware;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Elapsed time of each phase of one operation, in order, for a single log line (R-17).</summary>
internal sealed class PhaseTiming(TimeProvider clock)
{
    private readonly long _start = clock.GetTimestamp();
    private readonly List<string> _phases = [];
    private long _last = clock.GetTimestamp();

    public void Mark(string phase)
    {
        var now = clock.GetTimestamp();
        _phases.Add($"{phase}={(long)clock.GetElapsedTime(_last, now).TotalMilliseconds}ms");
        _last = now;
    }

    public string Describe() =>
        $"{string.Join(", ", _phases)}, total={(long)clock.GetElapsedTime(_start).TotalMilliseconds}ms";

    /// <summary>
    /// The level of the timing line: Warning once the operation lasted at least the slow-request threshold
    /// ("SlowRequestDiagnostics:Threshold", the same setting that flags a slow request), Information
    /// otherwise. The file log that feeds the UI only keeps Warning and above in production, so a slow
    /// preparation or launch is readable there while a fast one stays silent.
    /// </summary>
    public LogLevel LevelFor(IConfiguration configuration)
    {
        var threshold = configuration.GetSection("SlowRequestDiagnostics").Get<SlowRequestDiagnosticsOptions>()?.Threshold
            ?? new SlowRequestDiagnosticsOptions().Threshold;
        return threshold > TimeSpan.Zero && clock.GetElapsedTime(_start) >= threshold
            ? LogLevel.Warning
            : LogLevel.Information;
    }
}
