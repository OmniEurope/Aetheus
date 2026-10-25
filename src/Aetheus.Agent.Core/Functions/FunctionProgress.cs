// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Port of the former <c>run-with-progress.sh</c> script for the functions that run a long command: a
/// <c>==&gt; LABEL started</c> line, a <c>still running (Ns elapsed)</c> line every
/// <c>AETHEUS_PROGRESS_INTERVAL_SECONDS</c> (default 30, a positive integer), then
/// <c>finished in Ns (exit N)</c>. The command's output reaches the step log; cancelling the
/// function's token kills its process tree, as the script's signal traps killed the process group.
/// </summary>
internal static class FunctionProgress
{
    internal const string IntervalVariable = "AETHEUS_PROGRESS_INTERVAL_SECONDS";

    /// <summary>The interval, or null when the variable is set to anything but a positive integer
    /// (the script's usage error).</summary>
    public static TimeSpan? Interval(PipelineFunctionContext context)
    {
        var raw = FunctionShell.Variable(context, IntervalVariable) ?? "30";
        return raw.All(char.IsAsciiDigit)
               && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
               && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>Runs the command with progress lines; its exit code.</summary>
    public static async Task<int> RunAsync(
        IPipelineFunctionTools tools,
        PipelineFunctionContext context,
        string label,
        TimeSpan interval,
        ProcessStartInfo command,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await context.Log($"==> {label} started").ConfigureAwait(false);
        var result = await ProgressHeartbeat.RunAsync(
            () => tools.RunAsync(command, (line, level) => context.Log(line, level), cancellationToken),
            interval,
            elapsed => context.Log(string.Create(CultureInfo.InvariantCulture, $"==> {label} still running ({(long)elapsed.TotalSeconds}s elapsed)")),
            cancellationToken).ConfigureAwait(false);
        var exitCode = result.TimedOut ? 124 : result.ExitCode;
        var finished = (long)Stopwatch.GetElapsedTime(started).TotalSeconds;
        await context.Log(string.Create(CultureInfo.InvariantCulture, $"==> {label} finished in {finished}s (exit {exitCode})")).ConfigureAwait(false);
        return exitCode;
    }
}
