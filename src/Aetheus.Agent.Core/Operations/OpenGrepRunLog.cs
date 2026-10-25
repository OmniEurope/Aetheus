// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The log of one OpenGrep scan. The process runner streams every stderr line at
/// <see cref="TaskLogLevel.Error"/>, but OpenGrep writes its normal end-of-scan tables ("Scan Status",
/// "Scan Summary", "Ran N rules on M files") on stderr, so a clean scan filled the Security stage with
/// errors (recette R-17). This sink holds the stderr lines back until the process outcome is known:
/// <see cref="CompleteAsync"/> then logs them at <see cref="TaskLogLevel.Info"/> when the scan ended
/// normally (exit code 0 or one of the manifest's finding codes) and at <see cref="TaskLogLevel.Error"/>
/// otherwise (failure exit code, timeout, quota, cancellation). Other output passes through at once.
/// After a normal end it also names the files OpenGrep skipped for their size
/// (<see cref="OpenGrepSkippedFiles"/>).
/// </summary>
internal sealed class OpenGrepRunLog(Func<string, TaskLogLevel, Task> onOutput)
{
    /// <summary>A flood of stderr must not grow memory without bound: beyond this many held lines, the
    /// line is logged at once, at Error.</summary>
    internal const int MaxHeldLines = 5_000;

    private readonly Lock _gate = new();
    private readonly List<string> _held = [];

    /// <summary>The sink handed to the process runner in place of the step's own.</summary>
    internal Task Capture(string message, TaskLogLevel level)
    {
        if (level != TaskLogLevel.Error)
            return onOutput(message, level);
        lock (_gate)
        {
            if (_held.Count < MaxHeldLines)
            {
                _held.Add(message);
                return Task.CompletedTask;
            }
        }
        return onOutput(message, level);
    }

    /// <summary>Logs the held stderr lines, then (after a normal end, with the scanned source known) the
    /// files skipped for their size. Safe to call twice: the second call finds nothing held.</summary>
    internal async Task CompleteAsync(
        bool endedNormally, string? sourceDirectory, IReadOnlyList<string> excludePatterns)
    {
        string[] lines;
        lock (_gate)
        {
            lines = [.. _held];
            _held.Clear();
        }
        var level = endedNormally ? TaskLogLevel.Info : TaskLogLevel.Error;
        foreach (var line in lines)
            await onOutput(line, level).ConfigureAwait(false);
        if (!endedNormally || sourceDirectory is null || OpenGrepSkippedFiles.ParseSizeSkip(lines) is not { } skip)
            return;
        await onOutput(OpenGrepSkippedFiles.Describe(skip, sourceDirectory, excludePatterns), TaskLogLevel.Warning)
            .ConfigureAwait(false);
    }
}
