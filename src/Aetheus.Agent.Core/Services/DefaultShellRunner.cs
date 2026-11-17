// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging;

namespace Aetheus.Agent.Core.Services;

public sealed class DefaultShellRunner(ILogger<DefaultShellRunner> logger) : IShellRunner
{
    public async Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(isWindows ? "/c" : "-c");
        psi.ArgumentList.Add(command);

        return await RunForOutputAsync(psi, null, ct, timeout).ConfigureAwait(false);
    }

    public async Task<string> RunWithStdinAsync(string fileName, IReadOnlyList<string> args, string stdin, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // F-25: ArgumentList escapes each argument independently - no shell interpretation.
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return await RunForOutputAsync(psi, stdin, ct, timeout).ConfigureAwait(false);
    }

    private async Task<string> RunForOutputAsync(
        ProcessStartInfo psi,
        string? standardInput,
        CancellationToken ct,
        TimeSpan? timeout)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? AgentRuntimeDefaults.ShellCommandTimeout);

        using var process = Process.Start(psi)!;
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(
                    standardInput.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await Task.WhenAll(
                stdoutTask,
                stderrTask,
                process.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);
            return await stdoutTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }
    }

    public async Task<ShellExecResult> RunExecAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        => await RunExecCoreAsync(
            fileName, args, null, null, inheritEnvironment: true, null, null, ct, timeout).ConfigureAwait(false);

    public async Task<ShellExecResult> RunExecAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environmentVariables,
        string workingDirectory,
        bool inheritEnvironment,
        int maxCapturedOutputBytes,
        CancellationToken ct,
        TimeSpan? timeout = null)
        => await RunExecCoreAsync(
            fileName, args, environmentVariables, workingDirectory, inheritEnvironment,
            maxCapturedOutputBytes, null, ct, timeout)
            .ConfigureAwait(false);

    public async Task<ShellExecResult> RunExecStreamingAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environmentVariables,
        string workingDirectory,
        bool inheritEnvironment,
        int maxCapturedOutputBytes,
        Func<string, bool, Task> onLine,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        return await RunExecCoreAsync(
            fileName, args, environmentVariables, workingDirectory, inheritEnvironment,
            maxCapturedOutputBytes, onLine, ct, timeout)
            .ConfigureAwait(false);
    }

    private static ProcessStartInfo BuildStartInfo(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environmentVariables,
        string? workingDirectory,
        bool inheritEnvironment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        if (!inheritEnvironment)
        {
            psi.Environment.Clear();
            foreach (var key in RuntimeEnvironmentAllowList)
            {
                var value = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(value))
                    psi.Environment[key] = value;
            }
        }
        if (environmentVariables is not null)
        {
            foreach (var variable in environmentVariables)
                psi.Environment[variable.Key] = variable.Value;
        }
        // ArgumentList escapes each argument independently - no shell, no interpolation.
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private static Task<CapturedOutput> ReadChannelAsync(
        StreamReader reader,
        bool isStandardError,
        int? maxCapturedOutputBytes,
        Func<string, bool, Task>? onLine,
        CancellationToken ct) =>
        onLine is null
            ? ReadOutputAsync(reader, maxCapturedOutputBytes, ct)
            : ReadLinesAsync(reader, isStandardError, maxCapturedOutputBytes, onLine, ct);

    private async Task<ShellExecResult> RunExecCoreAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environmentVariables,
        string? workingDirectory,
        bool inheritEnvironment,
        int? maxCapturedOutputBytes,
        Func<string, bool, Task>? onLine,
        CancellationToken ct,
        TimeSpan? timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(args);

        var psi = BuildStartInfo(fileName, args, environmentVariables, workingDirectory, inheritEnvironment);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? AgentRuntimeDefaults.ShellCommandTimeout);

        using var process = Process.Start(psi)!;
        string stdout;
        string stderr;
        bool truncated;
        try
        {
            var stdoutTask = ReadChannelAsync(
                process.StandardOutput, false, maxCapturedOutputBytes, onLine, timeoutCts.Token);
            var stderrTask = ReadChannelAsync(
                process.StandardError, true, maxCapturedOutputBytes, onLine, timeoutCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);

            var capturedStdout = await stdoutTask.ConfigureAwait(false);
            var capturedStderr = await stderrTask.ConfigureAwait(false);
            stdout = capturedStdout.Value;
            stderr = capturedStderr.Value;
            truncated = capturedStdout.Truncated || capturedStderr.Truncated;
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        // M-2: keep the error channel observable (intrusion-detection / tampering signal)
        // instead of discarding it. Debug level - shell probes legitimately fail often.
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            var snippet = stderr.Length > 500 ? stderr[..500] : stderr;
            logger.LogDebug("Exec '{File}' exited {Exit}; stderr: {Stderr}", fileName, process.ExitCode, snippet.Trim());
        }

        return new ShellExecResult(process.ExitCode, stdout, stderr, truncated);
    }

    internal static async Task<CapturedOutput> ReadOutputAsync(
        StreamReader reader,
        int? maxCapturedOutputBytes,
        CancellationToken ct)
    {
        if (maxCapturedOutputBytes is null)
            return new CapturedOutput(await reader.ReadToEndAsync(ct).ConfigureAwait(false), false);

        var maximum = Math.Max(1, maxCapturedOutputBytes.Value);
        var retained = new StringBuilder(Math.Min(maximum, 8192));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            var remainingCharacters = maximum - retained.Length;
            if (remainingCharacters > 0)
                retained.Append(buffer, 0, Math.Min(read, remainingCharacters));
            if (read > remainingCharacters)
                truncated = true;
        }

        var value = retained.ToString();
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (bytes.Length > maximum)
        {
            value = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, maximum));
            truncated = true;
        }
        return new CapturedOutput(value, truncated);
    }

    /// <summary>
    /// Reads a stream line by line, handing each line to <paramref name="onLine"/> as soon as it
    /// arrives while still capturing a bounded copy of the text for the caller.
    /// </summary>
    internal static async Task<CapturedOutput> ReadLinesAsync(
        StreamReader reader,
        bool isStandardError,
        int? maxCapturedOutputBytes,
        Func<string, bool, Task> onLine,
        CancellationToken ct)
    {
        var maximum = Math.Max(1, maxCapturedOutputBytes ?? int.MaxValue);
        var retained = new StringBuilder();
        var retainedBytes = 0L;
        var truncated = false;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            // Whole lines only, counted in UTF-8 bytes like the non-streaming capture.
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (retainedBytes + lineBytes <= maximum)
            {
                retained.Append(line).Append('\n');
                retainedBytes += lineBytes;
            }
            else
            {
                truncated = true;
            }
            await onLine(line, isStandardError).ConfigureAwait(false);
        }
        return new CapturedOutput(retained.ToString(), truncated);
    }

    private static readonly string[] RuntimeEnvironmentAllowList =
    [
        "PATH",
        "PATHEXT",
        "SystemRoot",
        "ComSpec",
        "TEMP",
        "TMP",
        "LANG",
        "LC_ALL",
        "SSL_CERT_DIR",
        "SSL_CERT_FILE"
    ];

    internal readonly record struct CapturedOutput(string Value, bool Truncated);

    // A cancelled/timed-out run must not orphan the child: a stuck docker/systemctl/tar
    // would otherwise outlive `using process` and accumulate on the host. A tree it may not kill whole
    // (EPERM on a `sudo -n` child, reported as an AggregateException) is logged, never thrown over the
    // cancellation being propagated.
    private void KillTree(Process process) => ProcessTreeTerminator.Kill(process, logger);
}
