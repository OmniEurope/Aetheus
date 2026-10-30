// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R-100, R-101, R-102: every comparison of the front is OE's <c>OmniDiffViewer</c>, mounted through the
/// frame module (Monaco cannot run in the page under the strict CSP) with the previous and next change buttons. A
/// second diff viewer of our own, or a viewer that would load Monaco in the page, is refused here.
/// </summary>
public class DiffViewerUsageGuardTests
{
    private static readonly string Front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

    private static IEnumerable<(string Path, string Tag)> DiffViewerTags() =>
        RepositoryScan.Enumerate(Path.Combine(Front, "Components"), "*.razor")
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"<OmniDiffViewer\b[^>]*?/>", RegexOptions.Singleline)
                .Select(match => (path, match.Value)));

    [Fact]
    public void EveryDiffViewer_RunsThroughTheFrameModule_AndOffersNavigationBetweenChanges()
    {
        var tags = DiffViewerTags().ToList();

        Assert.True(tags.Count >= 10, $"Expected every comparison of the front to be listed, found {tags.Count}.");
        foreach (var (path, tag) in tags)
        {
            Assert.True(tag.Contains("InteropModulePath=\"@EditorModules.Frame\"", StringComparison.Ordinal),
                $"{Path.GetFileName(path)}: an OmniDiffViewer must run through EditorModules.Frame.");
            Assert.True(tag.Contains("ShowNavigation=\"true\"", StringComparison.Ordinal),
                $"{Path.GetFileName(path)}: an OmniDiffViewer must offer the previous and next change buttons.");
        }
    }

    [Fact]
    public void NoDiffViewerOfOurOwn_AndNoDiffEditorScriptOutsideTheFrame()
    {
        var components = RepositoryScan.Enumerate(Path.Combine(Front, "Components"), "*").ToList();
        Assert.DoesNotContain(components, path => Path.GetFileName(path).StartsWith("MonacoDiffViewer", StringComparison.Ordinal));
        Assert.DoesNotContain(components.Where(path => path.EndsWith(".razor", StringComparison.Ordinal)),
            path => File.ReadAllText(path).Contains("<MonacoDiffViewer", StringComparison.Ordinal));

        foreach (var script in new[] { "monaco-yaml.js", "monaco-frame-host.js" })
        {
            var source = File.ReadAllText(Path.Combine(Front, "wwwroot", "js", script));
            Assert.DoesNotContain("createDiffEditor", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DiffEditor", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheFrameModule_ExportsTheContractOmniDiffViewerCalls()
    {
        var source = File.ReadAllText(Path.Combine(Front, "wwwroot", "js", "omni-code-editor-frame.js"));

        foreach (var export in new[] { "setHeight", "load", "mountDiff", "setDiff", "configureDiff", "goToDiff", "disposeDiff" })
        {
            Assert.Matches($@"export (async )?function {export}\(", source);
        }
    }

    [Fact]
    public void EveryEditorAsksTheFrameModuleByItsOneConstant_SoABumpReachesThemAll()
    {
        var asked = Regex.Matches(string.Join('\n', RepositoryScan.Enumerate(Path.Combine(Front, "Components"), "*.razor").Select(File.ReadAllText)),
            @"InteropModulePath=""([^""]+)""").Select(match => match.Groups[1].Value).Distinct().ToList();

        Assert.Equal(["@EditorModules.Frame"], asked);
    }
}
