// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Runs a scanner within its shared timeout and storage quotas. Vulnerability-database scanners get
/// one bounded retry because interrupted OCI downloads are operational failures, not scan findings.
/// </summary>
internal sealed class ScannerProcessExecutionRunner(
    IScannerProcessRunner processRunner,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Runs the scanner. OpenGrep's stderr carries its normal end-of-scan tables, so for that
    /// scanner the lines are logged by <see cref="OpenGrepRunLog"/> once the outcome is known (Info after
    /// a normal end, Error otherwise); every other scanner keeps the process runner's Error-level stderr.
    /// <paramref name="sourceDirectory"/> lets the OpenGrep log name the files skipped for their size.</summary>
    internal async Task<(ExecutorResult Result, bool QuotaExceeded)> RunAsync(
        ScannerManifestEntry scanner,
        ProcessStartInfo process,
        string reportPath,
        string outputDirectory,
        string? cacheDirectory,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct,
        string? sourceDirectory = null)
    {
        if (!string.Equals(scanner.Key, OpenGrepSandbox.ScannerKey, StringComparison.OrdinalIgnoreCase))
            return await RunWithRetryAsync(
                scanner, process, reportPath, outputDirectory, cacheDirectory, timeoutSeconds, onOutput, ct)
                .ConfigureAwait(false);

        var log = new OpenGrepRunLog(onOutput);
        var endedNormally = false;
        try
        {
            var execution = await RunWithRetryAsync(
                scanner, process, reportPath, outputDirectory, cacheDirectory, timeoutSeconds, log.Capture, ct)
                .ConfigureAwait(false);
            endedNormally = EndedNormally(scanner, execution);
            return execution;
        }
        finally
        {
            await log.CompleteAsync(
                endedNormally, sourceDirectory, OpenGrepSkippedFiles.ExcludePatterns(scanner.Arguments))
                .ConfigureAwait(false);
        }
    }

    /// <summary>The scanner finished by itself: exit code 0 or one of its finding codes, within the
    /// timeout and the storage quotas. The same test <c>ScannerOperationExecutor</c> applies before it
    /// reads the report.</summary>
    private static bool EndedNormally(ScannerManifestEntry scanner, (ExecutorResult Result, bool QuotaExceeded) execution) =>
        !execution.QuotaExceeded
        && !execution.Result.TimedOut
        && (execution.Result.ExitCode == 0 || scanner.FindingExitCodes.Contains(execution.Result.ExitCode));

    private async Task<(ExecutorResult Result, bool QuotaExceeded)> RunWithRetryAsync(
        ScannerManifestEntry scanner,
        ProcessStartInfo process,
        string reportPath,
        string outputDirectory,
        string? cacheDirectory,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var startedAt = timeProvider.GetTimestamp();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var remainingSeconds = RemainingSeconds(startedAt, timeoutSeconds);
            if (remainingSeconds <= 0)
                return (new ExecutorResult(-1, true), false);

            var execution = await RunOnceAsync(
                process,
                outputDirectory,
                scanner.MaxReportBytes,
                cacheDirectory,
                scanner.MaxCacheBytes,
                remainingSeconds,
                onOutput,
                ct).ConfigureAwait(false);
            if (!ShouldRetry(scanner, execution, reportPath, attempt))
                return execution;

            if (File.Exists(reportPath))
                File.Delete(reportPath);
            await onOutput(
                $"Scanner {scanner.Name} vulnerability database download failed; retrying once after a bounded delay.",
                TaskLogLevel.Warning).ConfigureAwait(false);
            await Task.Delay(RetryDelay, timeProvider, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Scanner retry loop completed without a terminal result.");
    }

    private async Task<(ExecutorResult Result, bool QuotaExceeded)> RunOnceAsync(
        ProcessStartInfo process,
        string outputDirectory,
        long maxBytes,
        string? cacheDirectory,
        long maxCacheBytes,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        using var quotaCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var processTask = processRunner.RunAsync(process, timeoutSeconds, onOutput, quotaCancellation.Token);
        var quotaExceeded = false;
        while (!processTask.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, ct).ConfigureAwait(false);
            var outputExceeded = DirectorySize(outputDirectory, maxBytes) > maxBytes;
            var cacheExceeded = cacheDirectory is not null
                && DirectorySize(cacheDirectory, maxCacheBytes) > maxCacheBytes;
            if (!outputExceeded && !cacheExceeded)
                continue;
            quotaExceeded = true;
            await quotaCancellation.CancelAsync().ConfigureAwait(false);
            break;
        }

        try
        {
            var result = await processTask.ConfigureAwait(false);
            quotaExceeded |= DirectorySize(outputDirectory, maxBytes) > maxBytes
                || (cacheDirectory is not null && DirectorySize(cacheDirectory, maxCacheBytes) > maxCacheBytes);
            return (result, quotaExceeded);
        }
        catch (OperationCanceledException) when (quotaExceeded)
        {
            return (new ExecutorResult(-1, false), true);
        }
    }

    internal static long DirectorySize(string directory, long stopAfter)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { continue; }
            if (total > stopAfter)
                return total;
        }
        return total;
    }

    private int RemainingSeconds(long startedAt, int timeoutSeconds)
    {
        var remaining = timeoutSeconds - timeProvider.GetElapsedTime(startedAt).TotalSeconds;
        return remaining <= 0 ? 0 : Math.Max(1, (int)Math.Floor(remaining));
    }

    private static bool ShouldRetry(
        ScannerManifestEntry scanner,
        (ExecutorResult Result, bool QuotaExceeded) execution,
        string reportPath,
        int attempt)
    {
        if (attempt != 1
            || execution.QuotaExceeded
            || execution.Result.TimedOut
            || !string.Equals(scanner.Key, "trivy-dependencies", StringComparison.OrdinalIgnoreCase)
                && !scanner.Key.StartsWith("trivy-image", StringComparison.OrdinalIgnoreCase))
            return false;

        var functionalExit = execution.Result.ExitCode == 0
            || scanner.FindingExitCodes.Contains(execution.Result.ExitCode);
        return !functionalExit || !File.Exists(reportPath);
    }
}
