// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

internal static class WorkspaceFileMatcher
{
    public static IEnumerable<string> GetMatchingFiles(string baseDir, string pattern)
    {
        // Pipeline patterns are user-authored. Never let rooted or parent-relative paths escape the
        // agent workspace when collecting, substituting or publishing files.
        if (Path.IsPathRooted(pattern)
            || pattern.Split('/', '\\').Any(segment => segment == ".."))
            return [];

        var baseFull = Path.GetFullPath(baseDir);
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var searchPattern = Path.GetFileName(pattern);
            var searchDir = Path.GetDirectoryName(pattern)?.Replace("**", "").TrimStart(Path.DirectorySeparatorChar, '/');
            var recursive = pattern.Contains("**");
            return ExpandDirectories(baseDir, searchDir, baseFull)
                .SelectMany(directory => EnumerateConfinedFiles(directory, searchPattern, recursive, baseFull));
        }

        var fullPath = Path.Combine(baseDir, pattern);
        if (!IsUnderBase(fullPath, baseFull)) return [];
        if (File.Exists(fullPath)) return [fullPath];
        if (Directory.Exists(fullPath)) return EnumerateConfinedFiles(fullPath, "*", recursive: true, baseFull);
        return [];
    }

    /// <summary>
    /// The existing workspace directories the directory part of a pattern names. A segment holding
    /// <c>*</c> or <c>?</c> (<c>.qa-evidence/*-current/e2e</c>) matches the sibling directories it
    /// selects instead of being read as a literal name, which matched nothing (QA run 1283: the
    /// accessibility SARIF was there and the lint step published nothing). Every candidate stays
    /// confined to the workspace, reparse points included.
    /// </summary>
    private static List<string> ExpandDirectories(string baseDir, string? searchDir, string baseFull)
    {
        var segments = (searchDir ?? string.Empty).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = [baseDir];
        foreach (var segment in segments)
        {
            current = segment.Contains('*') || segment.Contains('?')
                ? current.SelectMany(directory => Directory.EnumerateDirectories(directory, segment, SearchOption.TopDirectoryOnly))
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : current.Select(directory => Path.Combine(directory, segment)).Where(Directory.Exists).ToList();
            current = current.Where(directory => IsUnderBase(directory, baseFull)).ToList();
        }
        return current.Where(Directory.Exists).ToList();
    }

    private static IEnumerable<string> EnumerateConfinedFiles(
        string searchDir,
        string searchPattern,
        bool recursive,
        string baseFull)
    {
        var pending = new Stack<string>();
        pending.Push(searchDir);
        while (pending.TryPop(out var current))
        {
            if (!IsUnderBase(current, baseFull))
                continue;

            foreach (var file in Directory.EnumerateFiles(current, searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (IsUnderBase(file, baseFull))
                    yield return file;
            }

            if (!recursive)
                continue;

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsUnderBase(directory, baseFull))
                    pending.Push(directory);
            }
        }
    }

    private static bool IsUnderBase(string candidate, string baseFull)
    {
        var full = Path.GetFullPath(candidate);
        if (!full.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(full, baseFull, StringComparison.Ordinal))
            return false;

        var relative = Path.GetRelativePath(baseFull, full);
        if (relative == ".")
            return !IsReparsePoint(baseFull);

        var cursor = baseFull;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && IsReparsePoint(cursor))
                return false;
        }
        return true;
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
