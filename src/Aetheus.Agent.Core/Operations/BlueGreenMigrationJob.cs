// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Services;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Runs the one-shot EF migration bundle job of a blue-green deployment and keeps the run log alive
/// while it works.
/// </summary>
/// <remarks>
/// A migration can legitimately take many minutes (an index build over a large table). A job that
/// only reported at its end left the run log silent for that whole time, indistinguishable from a
/// hang. The job's output is therefore forwarded line by line as the bundle prints it (the bundle
/// announces each "Applying migration" it starts), and a heartbeat names the elapsed time, the
/// migration in progress and the state Docker reports for the job's container. The step timeout is
/// not touched: this only makes the wait observable.
/// </remarks>
internal sealed class BlueGreenMigrationJob(
    IShellRunner shell, BlueGreenCompose compose, TimeProvider time, TimeSpan heartbeatInterval)
{
    /// <summary>How often the run log says the migration is still running.</summary>
    internal static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>Bound of the container-state lookup, which must never delay a heartbeat for long.</summary>
    private static readonly TimeSpan ContainerLookupTimeout = TimeSpan.FromSeconds(10);

    private static readonly Regex ApplyingMigration = new(
        @"Applying migration '([^']+)'", RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    internal async Task<ShellExecResult> RunAsync(
        BlueGreenContext context, string target, IReadOnlyList<string> composeArgs, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var current = new CurrentMigration();

        // The heartbeat stops with the timer: disposing it makes the next wait return false, so the
        // loop ends without a cancellation exception to swallow.
        using var timer = new PeriodicTimer(heartbeatInterval, time);
        var heartbeat = HeartbeatAsync(timer, context, target, started, current, onOutput, ct);
        try
        {
            return await compose.RunStreamingAsync(
                context, composeArgs, timeoutSeconds,
                (line, isStandardError) => ForwardLineAsync(line, isStandardError, current, onOutput),
                ct).ConfigureAwait(false);
        }
        finally
        {
            timer.Dispose();
            await heartbeat.ConfigureAwait(false);
        }
    }

    private static Task ForwardLineAsync(
        string line, bool isStandardError, CurrentMigration current, Func<string, TaskLogLevel, Task> onOutput)
    {
        var trimmed = line.TrimEnd('\r');
        if (trimmed.Length == 0) return Task.CompletedTask;

        if (ApplyingMigration.Match(trimmed) is { Success: true } applying)
            current.Name = applying.Groups[1].Value;

        return onOutput(
            trimmed.Length <= SmokeOperationExecutor.MaxLogLineLength
                ? trimmed
                : trimmed[..SmokeOperationExecutor.MaxLogLineLength] + " …[truncated]",
            isStandardError ? TaskLogLevel.Warning : TaskLogLevel.Info);
    }

    private async Task HeartbeatAsync(
        PeriodicTimer timer, BlueGreenContext context, string target, long started, CurrentMigration current,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var elapsed = time.GetElapsedTime(started);
            var container = await DescribeContainerAsync(context, target, ct).ConfigureAwait(false);
            await onOutput(Describe(elapsed, current.Name, container), TaskLogLevel.Info).ConfigureAwait(false);
        }
    }

    internal static string Describe(TimeSpan elapsed, string? migration, string container)
    {
        var minutes = (int)elapsed.TotalMinutes;
        var duration = minutes > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min {elapsed.Seconds} s")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.Seconds} s");
        var applying = migration is null ? "no migration reported yet" : $"applying {migration}";
        return $"Migration still running: {duration} elapsed, {applying}, job container: {container}.";
    }

    /// <summary>
    /// The state Docker reports for the job's container. A one-off `compose run` container is found by
    /// the labels Compose puts on it, because its generated name is not known to the caller.
    /// </summary>
    private async Task<string> DescribeContainerAsync(BlueGreenContext context, string target, CancellationToken ct)
    {
        var result = await shell.RunExecAsync(
            "docker",
            [
                "ps", "--all",
                "--filter", $"label=com.docker.compose.project={context.Project}",
                "--filter", $"label=com.docker.compose.service=back-{target}",
                "--filter", "label=com.docker.compose.oneoff=True",
                "--format", "{{.Status}}"
            ],
            ct, ContainerLookupTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0) return "state unavailable";
        var status = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrEmpty(status) ? "not found" : status;
    }

    /// <summary>The migration the bundle announced last; written by the output pump, read by the heartbeat.</summary>
    private sealed class CurrentMigration
    {
        private volatile string? _name;

        internal string? Name
        {
            get => _name;
            set => _name = value;
        }
    }
}
