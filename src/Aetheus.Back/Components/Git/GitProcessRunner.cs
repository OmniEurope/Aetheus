// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;
using Aetheus.Back.Configuration;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Low-level <c>git</c> process executor shared by <see cref="GitLightCliService"/> (read/admin
/// operations) and <see cref="GitLightCliWriter"/> (content-mutating operations). Extracted to a
/// real collaborator so the two services can share the runner without a <c>partial</c> split.
/// </summary>
public class GitProcessRunner(ILogger<GitProcessRunner> logger)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MissingDirectoriesReported =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan DefaultTimeout = BackendRuntimeDefaults.GitProcessTimeout;

    public async Task<(int ExitCode, string Output, string Error)> RunGitAsync(
        string workDir, IReadOnlyList<string> args, CancellationToken ct,
        TimeSpan? timeout = null, bool ignoreExitCode = false)
    {
        var result = await RunGitCoreAsync(
            workDir, args, ct, timeout, ignoreExitCode, maxOutputChars: null).ConfigureAwait(false);
        return (result.ExitCode, result.Output, result.Error);
    }

    public Task<(int ExitCode, string Output, string Error, bool OutputTruncated)> RunGitBoundedAsync(
        string workDir,
        IReadOnlyList<string> args,
        int maxOutputChars,
        CancellationToken ct,
        TimeSpan? timeout = null,
        bool ignoreExitCode = false) =>
        RunGitCoreAsync(
            workDir,
            args,
            ct,
            timeout,
            ignoreExitCode,
            Math.Clamp(maxOutputChars, 1, 4 * 1024 * 1024));

    private async Task<(int ExitCode, string Output, string Error, bool OutputTruncated)> RunGitCoreAsync(
        string workDir,
        IReadOnlyList<string> args,
        CancellationToken ct,
        TimeSpan? timeout,
        bool ignoreExitCode,
        int? maxOutputChars)
    {
        var psi = GitProcessStartInfoFactory.Create(workDir, args);

        // Guard a missing working directory before Process.Start: it otherwise throws a raw
        // Win32Exception (267, "the directory name is invalid") that bubbles out as an unhandled 500.
        // A repo registered in the DB but absent on disk (e.g. a fresh worktree whose runtime data/
        // folder was never populated) is a recoverable read miss, not a crash - return a clean
        // non-zero result that the callers already degrade on (empty tree/commits, surfaced error on writes).
        if (!Directory.Exists(workDir))
        {
            // Log an absent repository once per folder at Information within the process, with
            // repeated reads at Debug. Missing storage is a repository state shared by multiple
            // read requests.
            if (MissingDirectoriesReported.TryAdd(workDir, 0))
                logger.LogInformation("git {Args} skipped - working directory does not exist: {Dir} (reported once per folder)",
                    string.Join(' ', args), workDir);
            else
                logger.LogDebug("git {Args} skipped - working directory does not exist: {Dir}",
                    string.Join(' ', args), workDir);
            return (-1, string.Empty, $"Repository directory not found: {workDir}", false);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);

        string output, error;
        var outputTruncated = false;
        try
        {
            var outputTask = ReadOutputAsync(process.StandardOutput, maxOutputChars, cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            var waitTask = process.WaitForExitAsync(cts.Token);

            await Task.WhenAll(outputTask, errorTask, waitTask).ConfigureAwait(false);
            (output, outputTruncated) = await outputTask.ConfigureAwait(false);
            error = await errorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SafeKill(process);
            ct.ThrowIfCancellationRequested();
            logger.LogWarning("git {Args} timed out in {Dir}", string.Join(' ', args), workDir);
            return (-1, string.Empty, "Timeout", false);
        }

        if (process.ExitCode != 0 && !ignoreExitCode)
        {
            logger.LogDebug("git {Args} exited {Code} in {Dir}: {Error}",
                string.Join(' ', args), process.ExitCode, workDir, error);
        }

        return (process.ExitCode, output, error, outputTruncated);
    }

    private static async Task<(string Output, bool Truncated)> ReadOutputAsync(
        StreamReader reader,
        int? maxChars,
        CancellationToken ct)
    {
        if (maxChars is null)
            return (await reader.ReadToEndAsync(ct).ConfigureAwait(false), false);

        var retained = new StringBuilder(Math.Min(maxChars.Value, 64 * 1024));
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var remaining = maxChars.Value - retained.Length;
            if (remaining > 0)
                retained.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining)
                truncated = true;
        }
        return (retained.ToString(), truncated);
    }

    public static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
    }

    private void SafeKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { logger.LogWarning(ex, "[GitLight] Kill failed"); }
    }
}
