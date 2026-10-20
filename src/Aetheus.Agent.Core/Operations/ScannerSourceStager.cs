// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Projects scanner input into the agent-owned, Docker-visible work directory when systemd
/// <c>PrivateTmp</c> hides the pipeline workspace from the Docker daemon.
/// </summary>
internal static class ScannerSourceStager
{
    private const int MaxStagedSourceEntries = 200_000;
    private const long MaxStagedSourceBytes = 20L * 1024 * 1024 * 1024;

    // CA5392 asks for DefaultDllImportSearchPaths, which selects among Windows DLL search paths and
    // has no effect on the Unix loader; this call is Linux-only by construction.
    // CA2101 asks for an explicit string marshaling: given here rather than suppressed, because a
    // path is exactly the kind of argument where an implicit ANSI conversion silently mangles
    // non-ASCII characters, and this one names a file the scanner then reads.
    [SuppressMessage("Security", "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
        Justification = "libc on Linux; the attribute only constrains the Windows DLL search order.")]
    [DllImport("libc", SetLastError = true, EntryPoint = "link",
        CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    internal static string EnsureDockerVisibleSource(
        string sourceDirectory,
        string scanRoot,
        string workDirectory)
    {
        var workRoot = Path.GetFullPath(workDirectory);
        if (IsPathWithin(sourceDirectory, workRoot))
            return sourceDirectory;

        // Files are hard-linked when possible so large image archives remain zero-copy.
        // The bounded copy fallback covers cross-device files. Symlinks are preserved only
        // when their target remains inside the source tree and are never traversed.
        var stagedSource = Path.Combine(scanRoot, "source");
        StageSourceTree(sourceDirectory, stagedSource);
        return stagedSource;
    }

    internal static void StageSourceTree(
        string sourceDirectory,
        string destinationDirectory,
        bool useHardLinks = true,
        bool includeNodeModules = false)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Scanner source directory does not exist: {sourceRoot}");
        if (IsPathWithin(destinationRoot, sourceRoot) || IsPathWithin(sourceRoot, destinationRoot))
            throw new IOException("Scanner source staging directories must not overlap.");

        Directory.CreateDirectory(destinationRoot);
        ScannerFileSecurity.TrySetOwnerOnly(destinationRoot);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));
        var entryCount = 0;
        long totalBytes = 0;
        var enumeration = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        while (pending.Count > 0)
        {
            var (currentSource, currentDestination) = pending.Pop();
            foreach (var entry in new DirectoryInfo(currentSource).EnumerateFileSystemInfos("*", enumeration))
            {
                if (++entryCount > MaxStagedSourceEntries)
                    throw new IOException($"Scanner source exceeds the {MaxStagedSourceEntries} entry staging limit.");

                var relative = Path.GetRelativePath(sourceRoot, entry.FullName);
                if (ShouldExcludeFromProjection(relative, includeNodeModules))
                    continue;
                var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
                if (!IsPathWithin(destination, destinationRoot))
                    throw new IOException($"Scanner source entry escapes the staging directory: {relative}");

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    StageSafeSymbolicLink(entry, destination, sourceRoot);
                    continue;
                }

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(destination);
                    pending.Push((entry.FullName, destination));
                    continue;
                }

                var file = (FileInfo)entry;
                totalBytes = checked(totalBytes + file.Length);
                if (totalBytes > MaxStagedSourceBytes)
                    throw new IOException($"Scanner source exceeds the {MaxStagedSourceBytes} byte staging limit.");
                StageFile(file.FullName, destination, useHardLinks);
            }
        }
    }

    internal static bool ShouldExcludeFromProjection(
        string relativePath,
        bool includeNodeModules = false)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var isInstalledPackage = segments.Any(segment =>
            segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
        if (isInstalledPackage)
            return !includeNodeModules;

        return segments.Any(segment =>
            segment.Equals(".pipeline-artifacts", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("coverage", StringComparison.OrdinalIgnoreCase));
    }

    private static void StageSafeSymbolicLink(
        FileSystemInfo sourceLink,
        string destination,
        string sourceRoot)
    {
        var linkTarget = SafeLinkTarget(sourceLink.FullName, sourceLink.LinkTarget, sourceRoot);
        if ((sourceLink.Attributes & FileAttributes.Directory) != 0)
            Directory.CreateSymbolicLink(destination, linkTarget);
        else
            File.CreateSymbolicLink(destination, linkTarget);
    }

    /// <summary>The target of the symbolic link at <paramref name="linkPath"/>, kept as written, when it is
    /// relative and resolves inside <paramref name="sourceRoot"/>; any other link is refused.</summary>
    internal static string SafeLinkTarget(string linkPath, string? linkTarget, string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(linkTarget) || Path.IsPathRooted(linkTarget))
            throw new IOException($"Scanner source contains an unsafe symbolic link: {linkPath}");

        var resolvedTarget = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(linkPath) ?? sourceRoot,
            linkTarget));
        if (!IsPathWithin(resolvedTarget, sourceRoot))
            throw new IOException($"Scanner source symbolic link escapes the workspace: {linkPath}");
        return linkTarget;
    }

    private static void StageFile(string source, string destination, bool useHardLinks)
    {
        if (useHardLinks && !OperatingSystem.IsWindows() && CreateHardLinkUnix(source, destination) == 0)
            return;

        if (File.Exists(destination))
            File.Delete(destination);
        File.Copy(source, destination);
    }

    internal static bool IsPathWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalizedPath.Equals(normalizedRoot, comparison)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
}
