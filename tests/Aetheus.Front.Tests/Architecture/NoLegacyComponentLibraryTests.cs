// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Tests.Architecture;

/// <summary>Guards source, tests, assets and package manifests against the forbidden
/// component-library dependency. Documentation and pipeline definitions are outside this scan; the
/// diagnostic token is centralized in <see cref="ForbiddenName"/>.</summary>
public sealed class NoLegacyComponentLibraryTests
{
    /// <summary>The library's name, the single place the product still spells it.</summary>
    private const string ForbiddenName = "rad" + "zen";

    private static readonly string[] ScannedDirectories = ["src", "tests"];

    private static readonly string[] ScannedRootFiles = ["Directory.Packages.props", "Directory.Build.props"];

    private static readonly string[] SkippedSegments =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}monaco-editor{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}TestResults{Path.DirectorySeparatorChar}"
    ];

    // This file names what it forbids, and pipeline definitions and documentation are out of scope
    // (STD-TESTSCOPE): a test must not assert on their text.
    private static readonly string[] SkippedExtensions =
    [
        ".md", ".yml", ".yaml", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".svg",
        ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".dll", ".exe", ".pdb", ".zip", ".gz", ".br", ".wasm", ".dat", ".bin"
    ];

    private static readonly string[] ExcludedFiles =
    [
        Path.Combine("tests", "Aetheus.Front.Tests", "Architecture", "NoLegacyComponentLibraryTests.cs")
    ];

    [Fact]
    public void NoSourceFile_NamesTheRemovedLibrary()
    {
        var offenders = ScannedFiles()
            .Where(file => File.ReadAllText(file.Absolute).Contains(ForbiddenName, StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Relative)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "The former component library is removed from Aetheus (PLAN-008 lot 42). These files still name it:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoStylesheet_TargetsTheRemovedLibrarySelectors()
    {
        var css = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css");
        var offenders = RepositoryScan.Enumerate(css, "*.css")
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains(".rz-", StringComparison.Ordinal)
                       || text.Contains("--rz-", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(RepositoryScan.Root, file))
            .ToList();

        Assert.True(offenders.Count == 0,
            "The .rz-* stylesheets left with the frozen area:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheFrozenArea_AndItsAssets_AreGone()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var area = char.ToUpperInvariant(ForbiddenName[0]) + ForbiddenName[1..];

        Assert.False(Directory.Exists(Path.Combine(front, area + "Area")));
        Assert.False(File.Exists(Path.Combine(front, "wwwroot", "css", ForbiddenName + "-area.css")));
        Assert.False(File.Exists(Path.Combine(front, "wwwroot", "js", "a11y-" + ForbiddenName + ".js")));
    }

    [Fact]
    public void TheScan_CoversTheSources()
    {
        // Without this the guard above would pass over an empty set on any layout change.
        var files = ScannedFiles().ToList();

        Assert.True(files.Count > 1000, $"The scan only found {files.Count} files: it no longer covers the sources.");
        Assert.Contains(files, file => file.Relative == "Directory.Packages.props");
        Assert.Contains(files, file => file.Relative.EndsWith(".razor", StringComparison.Ordinal));
    }

    private static IEnumerable<(string Absolute, string Relative)> ScannedFiles()
    {
        var root = RepositoryScan.Root;

        foreach (var name in ScannedRootFiles)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) yield return (path, name);
        }

        foreach (var directory in ScannedDirectories)
        {
            var absolute = Path.Combine(root, directory);
            if (!Directory.Exists(absolute)) continue;

            // Through RepositoryScan, which refuses an empty scan: a guard whose path went wrong
            // would otherwise report green over nothing (ScanFloorGuardTests enforces this).
            foreach (var file in RepositoryScan.Enumerate(absolute, "*"))
            {
                if (SkippedSegments.Any(segment => file.Contains(segment, StringComparison.OrdinalIgnoreCase))) continue;
                if (SkippedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(file).StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)) continue;

                var relative = Path.GetRelativePath(root, file);
                if (ExcludedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;

                yield return (file, relative);
            }
        }
    }
}
