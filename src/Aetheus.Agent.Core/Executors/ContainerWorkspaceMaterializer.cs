// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Executors;

/// <summary>
/// Synchronizes a systemd-private checkout with agent state visible to the host container daemon.
/// Every container step refreshes from the prepared checkout and publishes its outputs back.
/// </summary>
internal static class ContainerWorkspaceMaterializer
{
    private const string ReadyMarker = ".aetheus-container-workspace-ready";
    private const string GitMetadataDirectory = ".git";
    private static readonly WorkspacePreparationLock PreparationLocks = new();

    internal static async Task<WorkspaceMaterialization> MaterializeAsync(
        int workspaceKey,
        string preparedWorkspace,
        string fallbackWorkspace,
        ILogger logger,
        WorkspaceSyncPlatform platform,
        CancellationToken cancellationToken)
    {
        if (platform.IsWindows
            || string.Equals(
                Path.GetFullPath(preparedWorkspace),
                Path.GetFullPath(fallbackWorkspace),
                StringComparison.Ordinal))
            return WorkspaceMaterialization.Success(preparedWorkspace);

        using var workspaceLock = await PreparationLocks.AcquireAsync(workspaceKey, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var marker = Path.Combine(fallbackWorkspace, ReadyMarker);
            if (!Directory.Exists(preparedWorkspace))
                return WorkspaceMaterialization.Failure(
                    fallbackWorkspace,
                    $"Prepared workspace '{preparedWorkspace}' is missing.");

            var isInitialized = File.Exists(marker);
            if (!isInitialized && Directory.Exists(fallbackWorkspace))
                Directory.Delete(fallbackWorkspace, recursive: true);
            Directory.CreateDirectory(fallbackWorkspace);
            OwnerOnlyDirectory.TrySet(fallbackWorkspace, (exception, directory) =>
                logger.LogWarning(
                    exception,
                    "Could not restrict workspace {Directory} to owner-only permissions",
                    directory));

            // PrivateTmp hides /tmp/<run>/s from the host daemon. Refresh before every container
            // step so intervening host stages are visible without discarding prior container output.
            // Git metadata is immutable for a pinned run and may be read-only, so materialize it once.
            var copy = await CopyTreeAsync(
                preparedWorkspace,
                fallbackWorkspace,
                platform.CopyProgram,
                cancellationToken,
                excludeProtectedMetadata: isInitialized).ConfigureAwait(false);
            if (!copy.IsSuccess)
                return WorkspaceMaterialization.Failure(
                    fallbackWorkspace,
                    $"Could not materialize the prepared workspace: {copy.FailureReason}");

            await File.WriteAllTextAsync(marker, workspaceKey.ToString(), cancellationToken)
                .ConfigureAwait(false);
            return WorkspaceMaterialization.Success(fallbackWorkspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WorkspaceMaterialization.Failure(
                fallbackWorkspace,
                $"Could not materialize the prepared workspace: {ex.Message}");
        }
    }

    internal static void ForgetWorkspace(int workspaceKey) =>
        PreparationLocks.ForgetIdle(workspaceKey);

    internal static async Task<WorkspaceMaterialization> SynchronizeBackAsync(
        int workspaceKey,
        string containerWorkspace,
        string preparedWorkspace,
        WorkspaceSyncPlatform platform,
        CancellationToken cancellationToken)
    {
        if (platform.IsWindows
            || string.Equals(
                Path.GetFullPath(containerWorkspace),
                Path.GetFullPath(preparedWorkspace),
                StringComparison.Ordinal))
            return WorkspaceMaterialization.Success(preparedWorkspace);

        using var workspaceLock = await PreparationLocks.AcquireAsync(workspaceKey, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(containerWorkspace))
                return WorkspaceMaterialization.Failure(
                    preparedWorkspace,
                    $"Container workspace '{containerWorkspace}' is missing.");
            Directory.CreateDirectory(preparedWorkspace);
            var copy = await CopyTreeAsync(
                containerWorkspace,
                preparedWorkspace,
                platform.CopyProgram,
                cancellationToken,
                excludeProtectedMetadata: true).ConfigureAwait(false);
            return copy.IsSuccess
                ? WorkspaceMaterialization.Success(preparedWorkspace)
                : WorkspaceMaterialization.Failure(
                    preparedWorkspace,
                    $"Could not publish container outputs: {copy.FailureReason}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WorkspaceMaterialization.Failure(
                preparedWorkspace,
                $"Could not publish container outputs: {ex.Message}");
        }
    }

    private static async Task<WorkspaceMaterialization> CopyTreeAsync(
        string source,
        string destination,
        string copyProgram,
        CancellationToken cancellationToken,
        bool excludeProtectedMetadata = false)
    {
        RemoveStaleEntries(source, destination, excludeProtectedMetadata);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if (excludeProtectedMetadata && IsProtectedSyncEntry(entry))
                continue;

            var psi = new ProcessStartInfo
            {
                FileName = copyProgram,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(entry);
            psi.ArgumentList.Add(destination);

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // The process completed between cancellation and the kill request.
                }

                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                return WorkspaceMaterialization.Failure(destination, "Workspace synchronization was cancelled or timed out.");
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode == 0)
                continue;

            var detail = (await stderr.ConfigureAwait(false)).Trim();
            return WorkspaceMaterialization.Failure(
                destination,
                string.IsNullOrEmpty(detail) ? $"cp exited with code {process.ExitCode}." : detail);
        }

        return WorkspaceMaterialization.Success(destination);
    }

    internal static void RemoveStaleEntries(
        string source,
        string destination,
        bool excludeProtectedMetadata)
    {
        foreach (var destinationEntry in Directory.EnumerateFileSystemEntries(destination))
        {
            if (excludeProtectedMetadata && IsProtectedSyncEntry(destinationEntry))
                continue;

            var sourceEntry = Path.Combine(source, Path.GetFileName(destinationEntry));
            var sourceIsFile = File.Exists(sourceEntry);
            var sourceIsDirectory = Directory.Exists(sourceEntry);
            var destinationIsDirectory = Directory.Exists(destinationEntry);
            if (sourceIsFile && !destinationIsDirectory)
                continue;
            if (sourceIsDirectory && destinationIsDirectory)
            {
                // `cp -a` merges into an existing directory, so a file deleted deeper in the source
                // would survive here and be published back. Recurse into real directories only: a
                // symbolic link is replaced by the copy, never followed into its target.
                if (!IsSymbolicLink(sourceEntry) && !IsSymbolicLink(destinationEntry))
                    RemoveStaleEntries(sourceEntry, destinationEntry, excludeProtectedMetadata);
                continue;
            }

            if (destinationIsDirectory)
                Directory.Delete(destinationEntry, recursive: true);
            else
                File.Delete(destinationEntry);
        }
    }

    // The step scripts are protected too: every container step of a run writes its /w/.prom-step-*.sh into
    // the one shared daemon-visible workspace after its own refresh, while a concurrent step may refresh
    // that same directory. A refresh that does not know a script would delete it as stale before the
    // container opens it ("can't open '/w/.prom-step-*.sh'"), and a publication would copy it into the
    // checkout.
    internal static bool IsProtectedSyncEntry(string entry)
    {
        var name = Path.GetFileName(entry);
        return name is GitMetadataDirectory or ReadyMarker
            || IsShellSafeScriptName(name);
    }

    // The shape of the step script names ContainerRunExecutor.CreateScriptName produces.
    internal static bool IsShellSafeScriptName(string scriptName) =>
        scriptName.StartsWith(".prom-step-", StringComparison.Ordinal)
        && scriptName.EndsWith(".sh", StringComparison.Ordinal)
        && scriptName.Length == 46
        && scriptName.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-');

    private static bool IsSymbolicLink(string path) => new DirectoryInfo(path).LinkTarget is not null;

}

internal sealed class WorkspacePreparationLock
{
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly object _gate = new();
    private readonly Action<int, Entry> _releaseReference;

    public WorkspacePreparationLock() => _releaseReference = ReleaseReference;

    internal int EntryCount
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    internal async Task<Releaser> AcquireAsync(int workspaceKey, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(workspaceKey, out entry!))
            {
                entry = new Entry();
                _entries[workspaceKey] = entry;
            }
            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(_releaseReference, workspaceKey, entry);
        }
        catch
        {
            ReleaseReference(workspaceKey, entry);
            throw;
        }
    }

    internal void ForgetIdle(int workspaceKey)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(workspaceKey, out var entry) && entry.RefCount == 0)
            {
                _entries.Remove(workspaceKey);
                entry.Semaphore.Dispose();
            }
        }
    }

    private void ReleaseReference(int workspaceKey, Entry entry)
    {
        lock (_gate)
        {
            entry.RefCount--;
            if (entry.RefCount == 0
                && _entries.TryGetValue(workspaceKey, out var current)
                && ReferenceEquals(current, entry))
            {
                _entries.Remove(workspaceKey);
                entry.Semaphore.Dispose();
            }
        }
    }

    internal sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int RefCount;
    }

    /// <summary>Releases the workspace's slot. It reaches the lock only through the release delegate it is
    /// given, so the lock and its releaser do not depend on each other.</summary>
    internal readonly struct Releaser(
        Action<int, Entry> releaseReference,
        int workspaceKey,
        Entry entry) : IDisposable
    {
        public void Dispose()
        {
            entry.Semaphore.Release();
            releaseReference(workspaceKey, entry);
        }
    }
}

internal sealed record WorkspaceMaterialization(
    bool IsSuccess,
    string Workspace,
    string? FailureReason)
{
    internal static WorkspaceMaterialization Success(string workspace) =>
        new(true, workspace, null);

    internal static WorkspaceMaterialization Failure(string workspace, string reason) =>
        new(false, workspace, reason);
}

/// <summary>
/// How a container workspace is synchronized: on Windows nothing is copied and the prepared workspace is
/// used as is; elsewhere every top-level entry is copied with <see cref="CopyProgram"/> run as
/// <c>&lt;program&gt; -a &lt;entry&gt; &lt;destination&gt;</c>. <see cref="Current"/> is the agent's own platform and
/// <c>cp</c>; tests replace both to run the synchronization on any platform.
/// </summary>
internal sealed record WorkspaceSyncPlatform(bool IsWindows, string CopyProgram)
{
    internal static WorkspaceSyncPlatform Current { get; } = new(OperatingSystem.IsWindows(), "cp");
}
