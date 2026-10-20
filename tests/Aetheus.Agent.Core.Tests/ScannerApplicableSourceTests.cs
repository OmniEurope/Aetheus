// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Which source trees make a scanner applicable: a file with one of its extensions, searched
/// recursively outside tool, build, dependency and analysis output folders.</summary>
public sealed class ScannerApplicableSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-scanner-source-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void NoDeclaredExtension_IsAlwaysApplicable()
    {
        Assert.True(ScannerOperationValidator.HasApplicableSource(new ScannerManifestEntry(), _root));
    }

    [Fact]
    public void MatchingFileInNestedFolder_IsApplicable_WhateverTheExtensionCase()
    {
        Write("src", "app", "Program.CS");

        Assert.True(ScannerOperationValidator.HasApplicableSource(Scanner(".cs"), _root));
    }

    [Fact]
    public void NoMatchingFile_IsNotApplicable()
    {
        Write("src", "readme.md");

        Assert.False(ScannerOperationValidator.HasApplicableSource(Scanner(".cs"), _root));
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".vs")]
    [InlineData(".claude")]
    [InlineData(".Codex")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".pipeline-artifacts")]
    [InlineData(".analysis-image")]
    [InlineData(".analysis-work")]
    [InlineData(".qa-evidence")]
    public void MatchingFileOnlyInSkippedFolder_IsNotApplicable(string folder)
    {
        Write(folder, "nested", "Program.cs");

        Assert.False(ScannerOperationValidator.HasApplicableSource(Scanner(".cs"), _root));
    }

    [Theory]
    [InlineData(".codex")]
    [InlineData("Bin")]
    [InlineData("analysis-image")]
    [InlineData("qa-evidence")]
    public void SkippedNamesAreMatchedExactly(string folder)
    {
        Write(folder, "Program.cs");

        Assert.True(ScannerOperationValidator.HasApplicableSource(Scanner(".cs"), _root));
    }

    private static ScannerManifestEntry Scanner(params string[] extensions) =>
        new() { FileExtensions = [.. extensions] };

    private void Write(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }
}
