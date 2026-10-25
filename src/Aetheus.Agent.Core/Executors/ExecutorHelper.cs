// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;

namespace Aetheus.Agent.Core.Executors;

public static class ExecutorHelper
{
    // At-most-once across the process: flags the log-forwarding-failure condition without spamming
    // a line per dropped output line (a wedged backend would otherwise flood the agent log).
    private static int _forwardFailureLogged;

    internal const string StderrPrefix = "[stderr] ";

    /// <summary>
    /// Streams process output line-by-line to the callback. Log-forwarding failures are
    /// swallowed so a transient backend outage cannot kill the process or mask its exit code;
    /// the first such failure is logged once (best-effort) when a <paramref name="logger"/> is given.
    /// </summary>
    public static async Task StreamOutputAsync(
        StreamReader reader,
        TaskLogLevel level,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct,
        ILogger? logger = null)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            try
            {
                var classified = ClassifyLine(line, level);
                // A standard-error line no longer logged as an Error keeps the mark of its channel in its
                // text (the "[stderr]" convention of ProcessRunner): lowering its level loses no information.
                var text = level == TaskLogLevel.Error && classified != TaskLogLevel.Error ? StderrPrefix + line : line;
                await onOutput(text, classified).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Best-effort: log forwarding is not worth killing the process over. The real exit
                // code must be preserved. Log once per process to surface the condition; the
                // PollingService FlushAsync catch logs the flush failure separately.
                if (Interlocked.Exchange(ref _forwardFailureLogged, 1) == 0)
                    logger?.LogDebug(ex, "Log line forwarding failed; suppressing further occurrences (exit code preserved).");
            }
        }
    }

    /// <summary>
    /// The level a line is logged at. Standard error is handed over as <see cref="TaskLogLevel.Error"/> (the
    /// channel, not a verdict) and reconsidered here (R-36): the journalctl status notices are Info, a line
    /// that states its own severity gets it, every other line is a Warning (see
    /// <see cref="StderrLineClassifier"/>). Standard output and any other level are never touched.
    /// </summary>
    internal static TaskLogLevel ClassifyLine(string line, TaskLogLevel level) =>
        level != TaskLogLevel.Error ? level
        : IsBenignNotice(line) ? TaskLogLevel.Info
        : StderrLineClassifier.Classify(line);

    private static bool IsBenignNotice(string line) =>
        // journalctl prints a "Hint:" status line on stderr when it has nothing more to show.
        line.Contains("Hint:", StringComparison.Ordinal)
        // "Pass -q / --quiet to disable this notice" - the advice that accompanies the Hint above.
        || line.Contains("Pass -q", StringComparison.Ordinal)
        || line.Contains("Pass --quiet", StringComparison.Ordinal)
        // No matching journal entries / unreadable journal - an empty result, not a failure.
        || line.Contains("No journal files were opened", StringComparison.Ordinal)
        || line.Contains("-- No entries --", StringComparison.Ordinal);
}
