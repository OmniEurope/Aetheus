// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Transactional filesystem operations for agent self-update. Reparse points are removed without
/// being followed, operator configuration is preserved, and a failed replacement restores the
/// complete nested binary payload from the bounded rollback snapshot.
/// </summary>
internal static class AgentSelfUpdateFileSystem
{
    internal static void PrepareCleanDirectory(string path)
    {
        var existing = new DirectoryInfo(path);
        existing.Refresh();
        if (existing.Exists || existing.LinkTarget is not null)
            DeleteEntryWithoutFollowingLinks(existing);
        Directory.CreateDirectory(path);
    }

    internal static bool IsSameOrChildPath(string candidate, string root)
    {
        var candidateFull = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(candidateFull, rootFull, comparison)
            || candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison);
    }

    internal static void SnapshotRollback(string installDir, string rollbackDir)
    {
        PrepareCleanDirectory(rollbackDir);
        foreach (var source in EnumeratePayloadFiles(installDir))
        {
            var relativePath = Path.GetRelativePath(installDir, source);
            if (IsOperatorConfiguration(relativePath)
                || relativePath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyPayloadFile(source, rollbackDir, relativePath, overwrite: true);
        }

        var currentVersion = typeof(AgentSelfUpdateFileSystem).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        File.WriteAllText(Path.Combine(rollbackDir, "rollback-version.txt"), currentVersion);
    }

    internal static void ReplaceInstallFilesWithRollback(
        string sourceDir,
        string installDir,
        string rollbackDir,
        Action<string>? beforeCopy = null)
    {
        try
        {
            ClearInstallPayload(installDir);
            foreach (var source in EnumeratePayloadFiles(sourceDir)
                         .OrderBy(path => Path.GetRelativePath(sourceDir, path), StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(sourceDir, source);
                if (IsOperatorConfiguration(relativePath)) continue;

                beforeCopy?.Invoke(relativePath);
                CopyPayloadFile(source, installDir, relativePath, overwrite: false);
            }
        }
        catch (Exception updateException) when (updateException is IOException or UnauthorizedAccessException)
        {
            try
            {
                RestoreRollback(installDir, rollbackDir);
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "Agent binary replacement failed and automatic rollback also failed.",
                    new AggregateException(updateException, rollbackException));
            }

            throw new InvalidOperationException(
                "Agent binary replacement failed; the previous binaries were restored automatically.",
                updateException);
        }
    }

    internal static void RestoreRollback(string installDir, string rollbackDir)
    {
        if (!Directory.Exists(rollbackDir))
            throw new IOException($"Rollback directory does not exist: {rollbackDir}");

        var rollbackFiles = EnumeratePayloadFiles(rollbackDir)
            .Where(path => !string.Equals(
                Path.GetRelativePath(rollbackDir, path), "rollback-version.txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(rollbackDir, path), StringComparer.Ordinal)
            .ToList();
        if (rollbackFiles.Count == 0)
            throw new IOException($"Rollback directory contains no agent binaries: {rollbackDir}");

        ClearInstallPayload(installDir);
        foreach (var rollbackFile in rollbackFiles)
            CopyPayloadFile(
                rollbackFile, installDir, Path.GetRelativePath(rollbackDir, rollbackFile), overwrite: true);
    }

    private static IEnumerable<string> EnumeratePayloadFiles(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        });

    private static bool IsOperatorConfiguration(string relativePath) =>
        string.Equals(relativePath, "appsettings.json", StringComparison.OrdinalIgnoreCase);

    private static void CopyPayloadFile(string source, string destinationRoot, string relativePath, bool overwrite)
    {
        var destination = Path.Combine(destinationRoot, relativePath);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.Copy(source, destination, overwrite);
    }

    private static void ClearInstallPayload(string installDir)
    {
        foreach (var entry in new DirectoryInfo(installDir).EnumerateFileSystemInfos())
        {
            if (entry is FileInfo && IsOperatorConfiguration(entry.Name)) continue;
            DeleteEntryWithoutFollowingLinks(entry);
        }
    }

    private static void DeleteEntryWithoutFollowingLinks(FileSystemInfo entry)
    {
        entry.Refresh();
        var linkTarget = entry.LinkTarget;
        if (!entry.Exists && linkTarget is null) return;

        if (linkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (entry is DirectoryInfo)
                Directory.Delete(entry.FullName, recursive: false);
            else
                File.Delete(entry.FullName);
            return;
        }

        if (entry is FileInfo file)
        {
            file.Delete();
            return;
        }

        var directory = (DirectoryInfo)entry;
        foreach (var child in directory.EnumerateFileSystemInfos())
            DeleteEntryWithoutFollowingLinks(child);
        directory.Delete();
    }
}
