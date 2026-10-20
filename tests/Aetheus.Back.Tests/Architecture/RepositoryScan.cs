// kit-model RepositoryScan 2
// SPDX-License-Identifier: EUPL-1.2
//
// File enumeration for repository-wide guard tests, with one property the raw
// Directory.EnumerateFiles does not have: it refuses to return nothing.
//
// Why: a guard that scans the repository and finds no file passes. Every assertion it makes is
// vacuously true over an empty set, so it reports green while protecting nothing (seen in Aetheus:
// a guard excluded the whole repository when the suite ran from a worktree).
//
// Copy as is next to the guards that use it (FileSizeAuditTests) and adjust only the namespace. The
// repository root is the folder holding global.json, found by walking up from the test binary, so no
// path constant is needed.
// Keep the first line of this file in the copy: it tells the kit's verify-rules.ps1 which version of
// the model the copy implements (STD-KITCOPY).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Aetheus.Back.Tests.Architecture;

internal static class RepositoryScan
{
    /// <summary>The repository root: the nearest ancestor of the test binary holding global.json.</summary>
    internal static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new InvalidOperationException("Could not locate the repository root (global.json).");
        }
    }

    /// <summary>Enumerates and fails loudly when the scan is empty.</summary>
    internal static IReadOnlyList<string> Enumerate(string directory, string pattern)
    {
        var files = EnumerateOptional(directory, pattern);
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Scanning '{directory}' for '{pattern}' returned no file. A guard that scans nothing "
                + "passes vacuously, so this is reported as a failure rather than a green run.");
        }

        return files;
    }

    /// <summary>
    /// Enumerates without the floor, skipping build output. Only for one leg of a sweep whose total
    /// the caller still proves non-empty.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateOptional(string directory, string pattern)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Where(file => !IsBuildOutput(Path.DirectorySeparatorChar + Path.GetRelativePath(directory, file)))
                .ToList()
            : [];

    // Judged on the path below the scanned directory: the checkout running the scan may itself live
    // under .worktrees, which would otherwise exclude every file.
    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}node_modules{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}.git{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}.worktrees{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}.claude{separator}worktrees{separator}", StringComparison.OrdinalIgnoreCase);
    }

    // Aetheus extension beyond the kit model (candidate for the model): the model only scans recursively.

    /// <summary>
    /// Enumerates the files directly in <paramref name="directory"/>, not its subfolders, and fails
    /// loudly when the scan is empty, like <see cref="Enumerate"/>.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateTopLevel(string directory, string pattern)
    {
        var files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToList()
            : [];
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Scanning '{directory}' for '{pattern}' returned no file. A guard that scans nothing "
                + "passes vacuously, so this is reported as a failure rather than a green run.");
        }

        return files;
    }
}
