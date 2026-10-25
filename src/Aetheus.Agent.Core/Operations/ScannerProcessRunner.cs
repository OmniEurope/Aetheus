// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aetheus.Agent.Core.Operations;

public sealed class ScannerProcessRunner(
    IOptions<Configuration.AetheusAgentOptions> options,
    ILogger<ScannerProcessRunner> logger) : IScannerProcessRunner
{
    private static readonly TimeSpan s_monitorShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly Meter s_meter = new("Aetheus.Analysis.Agent");
    private static readonly Histogram<double> s_duration = s_meter.CreateHistogram<double>(
        "aetheus.analysis.scanner.duration", "s", "Elapsed scanner execution time.");
    private static readonly Histogram<double> s_cpu = s_meter.CreateHistogram<double>(
        "aetheus.analysis.scanner.cpu", "%", "Peak scanner container CPU percentage.");
    private static readonly Histogram<long> s_memory = s_meter.CreateHistogram<long>(
        "aetheus.analysis.scanner.memory", "By", "Peak scanner container memory usage.");
    private static readonly Histogram<long> s_disk = s_meter.CreateHistogram<long>(
        "aetheus.analysis.scanner.disk", "By", "Scanner container block I/O bytes.");
    private static readonly Histogram<long> s_network = s_meter.CreateHistogram<long>(
        "aetheus.analysis.scanner.network", "By", "Scanner container network I/O bytes.");
    private readonly Configuration.AetheusAgentOptions _options = options.Value;

    public async Task<ExecutorResult> RunAsync(
        ProcessStartInfo processInfo,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        using var process = new Process { StartInfo = processInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        var containerName = TryGetContainerName(processInfo);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        process.Start();
        var monitor = containerName is null
            ? Task.FromResult(ContainerUsage.Empty)
            : MonitorContainerAsync(containerName, ReadDockerStatsAsync, TimeProvider.System, monitorCancellation.Token);
        var stdout = ExecutorHelper.StreamOutputAsync(process.StandardOutput, TaskLogLevel.Info, onOutput, timeout.Token, logger);
        var stderr = ExecutorHelper.StreamOutputAsync(process.StandardError, TaskLogLevel.Error, onOutput, timeout.Token, logger);
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await monitorCancellation.CancelAsync().ConfigureAwait(false);
            var usage = await AwaitMonitorAsync(monitor).ConfigureAwait(false);
            await PublishUsageAsync(stopwatch.Elapsed, usage, onOutput).ConfigureAwait(false);
            return new ExecutorResult(process.ExitCode, false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { logger.LogDebug(ex, "Scanner process kill failed"); }
            await monitorCancellation.CancelAsync().ConfigureAwait(false);
            var usage = await AwaitMonitorAsync(monitor).ConfigureAwait(false);
            await PublishUsageAsync(stopwatch.Elapsed, usage, onOutput).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            return new ExecutorResult(-1, true);
        }
    }

    private async Task<ContainerUsage> AwaitMonitorAsync(Task<ContainerUsage> monitor)
    {
        try
        {
            if (!await CompletesWithinAsync(monitor, s_monitorShutdownTimeout).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "Scanner resource monitor did not stop within {TimeoutSeconds}s; continuing task finalization without usage metrics",
                    s_monitorShutdownTimeout.TotalSeconds);
                return ContainerUsage.Empty;
            }

            return await monitor.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Scanner resource monitor failed during task finalization");
            return ContainerUsage.Empty;
        }
    }

    internal static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string? TryGetContainerName(ProcessStartInfo process)
    {
        if (!string.Equals(process.FileName, "docker", StringComparison.OrdinalIgnoreCase)) return null;
        for (var index = 0; index < process.ArgumentList.Count - 1; index++)
            if (string.Equals(process.ArgumentList[index], "--name", StringComparison.Ordinal))
                return process.ArgumentList[index + 1];
        return null;
    }

    /// <summary>Samples the container every five seconds until <paramref name="ct"/> is cancelled and keeps
    /// the peak of each figure. A sample whose reading fails (<c>null</c>) or does not parse is skipped.
    /// <paramref name="readStats"/> is <see cref="ReadDockerStatsAsync"/> outside tests.</summary>
    internal static async Task<ContainerUsage> MonitorContainerAsync(
        string containerName,
        Func<string, CancellationToken, Task<string?>> readStats,
        TimeProvider clock,
        CancellationToken ct)
    {
        var usage = ContainerUsage.Empty;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var output = await readStats(containerName, ct).ConfigureAwait(false);
                if (output is not null) usage = usage.WithPeakOf(output);
                await Task.Delay(TimeSpan.FromSeconds(5), clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return usage;
    }

    /// <summary>One <c>docker stats</c> reading of the container, or <c>null</c> when docker could not be
    /// started or failed.</summary>
    private static Task<string?> ReadDockerStatsAsync(string containerName, CancellationToken ct) =>
        ReadOutputAsync(DockerStatsStartInfo(containerName), ct);

    internal static ProcessStartInfo DockerStatsStartInfo(string containerName)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "stats", "--no-stream", "--format",
                     "{{.CPUPerc}}|{{.MemUsage}}|{{.BlockIO}}|{{.NetIO}}", containerName
                 })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    /// <summary>The standard output of the process, once it has exited with code 0; <c>null</c> when it
    /// could not be started or exited with another code.</summary>
    internal static async Task<string?> ReadOutputAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = Process.Start(startInfo);
        if (process is null) return null;
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0 ? output : null;
    }

    private static async Task PublishUsageAsync(
        TimeSpan elapsed,
        ContainerUsage usage,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        s_duration.Record(elapsed.TotalSeconds);
        if (usage.PeakCpuPercent > 0) s_cpu.Record(usage.PeakCpuPercent);
        if (usage.PeakMemoryBytes > 0) s_memory.Record(usage.PeakMemoryBytes);
        if (usage.DiskReadBytes + usage.DiskWrittenBytes > 0)
            s_disk.Record(usage.DiskReadBytes + usage.DiskWrittenBytes);
        if (usage.NetworkReceivedBytes + usage.NetworkSentBytes > 0)
            s_network.Record(usage.NetworkReceivedBytes + usage.NetworkSentBytes);
        await onOutput(
            $"Scanner resource usage: duration={elapsed.TotalSeconds:0.###}s, peakCpu={usage.PeakCpuPercent:0.##}%, " +
            $"peakMemory={usage.PeakMemoryBytes}B, swap=unavailable, diskRead={usage.DiskReadBytes}B, " +
            $"diskWritten={usage.DiskWrittenBytes}B, networkReceived={usage.NetworkReceivedBytes}B, " +
            $"networkSent={usage.NetworkSentBytes}B.",
            TaskLogLevel.Info).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "scanner.duration", "Duration", "s", elapsed.TotalSeconds).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "scanner.cpu.peak", "Percentage", "%", usage.PeakCpuPercent).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "scanner.memory.peak", "Size", "bytes", usage.PeakMemoryBytes).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "scanner.disk.read", "Size", "bytes", usage.DiskReadBytes).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "scanner.disk.written", "Size", "bytes", usage.DiskWrittenBytes).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "scanner.network.received", "Size", "bytes", usage.NetworkReceivedBytes).ConfigureAwait(false);
        await PipelineMetricEmitter.EmitAsync(onOutput, "scanner.network.sent", "Size", "bytes", usage.NetworkSentBytes).ConfigureAwait(false);
    }

    internal sealed record ContainerUsage(
        double PeakCpuPercent,
        long PeakMemoryBytes,
        long DiskReadBytes,
        long DiskWrittenBytes,
        long NetworkReceivedBytes,
        long NetworkSentBytes)
    {
        public static ContainerUsage Empty { get; } = new(0, 0, 0, 0, 0, 0);

        /// <summary>This usage raised to the figures of one <c>docker stats</c> line; unchanged when the
        /// line does not parse.</summary>
        public ContainerUsage WithPeakOf(string statsLine) =>
            DockerStatsParser.TryParseUsage(statsLine, out var cpu, out var memory, out var readBytes, out var writtenBytes,
                out var receivedBytes, out var sentBytes)
                ? new ContainerUsage(
                    Math.Max(PeakCpuPercent, cpu),
                    Math.Max(PeakMemoryBytes, memory),
                    Math.Max(DiskReadBytes, readBytes),
                    Math.Max(DiskWrittenBytes, writtenBytes),
                    Math.Max(NetworkReceivedBytes, receivedBytes),
                    Math.Max(NetworkSentBytes, sentBytes))
                : this;
    }
}
