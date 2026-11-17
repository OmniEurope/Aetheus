// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Services;

/// <summary>Result of an argv (shell-free) process execution.</summary>
public sealed record ShellExecResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool Truncated = false);

/// <summary>
/// Cross-platform shell execution abstraction. Picks <c>cmd.exe</c> on Windows and <c>/bin/bash</c>
/// elsewhere, drains stdout/stderr concurrently to avoid deadlocks, and enforces a per-call timeout.
/// </summary>
public interface IShellRunner
{
    /// <summary>
    /// Runs the supplied shell command and returns stdout. Stderr is drained but not returned -
    /// callers that need it should pipe it (<c>2>&amp;1</c>) or extend the API.
    /// </summary>
    /// <param name="command">Shell command line (will be passed to <c>/c</c> or <c>-c</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="timeout">Optional per-call timeout. Defaults to 15s when null.</param>
    [System.Obsolete("Use RunExecAsync with ArgumentList to avoid shell injection. This method uses bash -c / cmd /c.")]
    Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null);

    /// <summary>
    /// F-25: launches an executable with each argument passed via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// (no shell, no string interpolation) and pipes <paramref name="stdin"/> to its standard input.
    /// Use this whenever an argument or piped payload contains user-controlled data, to defeat
    /// shell injection. Returns stdout; stderr is drained.
    /// </summary>
    Task<string> RunWithStdinAsync(string fileName, IReadOnlyList<string> args, string stdin, CancellationToken ct, TimeSpan? timeout = null);

    /// <summary>
    /// Launches <paramref name="fileName"/> with each argument passed via
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> - <b>no shell, no string
    /// interpolation</b>, so shell metacharacters in arguments cannot inject commands. Returns
    /// the exit code, stdout and stderr; stderr is kept observable (logged on non-zero exit)
    /// rather than swallowed via <c>2&gt;/dev/null</c>. Prefer this over <see cref="RunAsync"/>
    /// for any command that embeds dynamic or untrusted input (file paths, names parsed from
    /// other command output, etc.) and whenever the exit code matters.
    /// </summary>
    Task<ShellExecResult> RunExecAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null);

    /// <summary>
    /// Runs an executable in an explicit directory with an explicit environment. When
    /// <paramref name="inheritEnvironment"/> is false, only a small runtime allow-list plus the
    /// supplied variables is exposed to the child process.
    /// </summary>
    Task<ShellExecResult> RunExecAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environmentVariables,
        string workingDirectory,
        bool inheritEnvironment,
        int maxCapturedOutputBytes,
        CancellationToken ct,
        TimeSpan? timeout = null);

    /// <summary>
    /// Same as the environment-aware <c>RunExecAsync</c>, but hands every stdout and stderr line to
    /// <paramref name="onLine"/> (second argument: true for stderr) as soon as the process writes it,
    /// instead of only returning the whole output at the end. The result still carries a bounded
    /// capture of both channels. A runner that cannot stream falls back to replaying the captured
    /// lines after the process exited; <see cref="DefaultShellRunner"/> streams live.
    /// </summary>
    async Task<ShellExecResult> RunExecStreamingAsync(
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
        var result = await RunExecAsync(
            fileName, args, environmentVariables, workingDirectory, inheritEnvironment,
            maxCapturedOutputBytes, ct, timeout).ConfigureAwait(false);
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            await onLine(line.TrimEnd('\r'), false).ConfigureAwait(false);
        foreach (var line in result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            await onLine(line.TrimEnd('\r'), true).ConfigureAwait(false);
        return result;
    }
}
