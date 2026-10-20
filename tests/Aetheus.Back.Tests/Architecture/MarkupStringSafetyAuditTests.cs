// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>MarkupString content must originate from encoded HTML or a Markdig pipeline with
/// DisableHtml to prevent executable raw markup.</summary>
public sealed class MarkupStringSafetyAuditTests
{
    /// <summary>What makes a MarkupString safe, in the file that produces it.</summary>
    private static readonly string[] SafeSources = ["HtmlEncode", "DisableHtml()", "MarkdownRenderer"];

    [Fact]
    public void Every_MarkupString_Comes_From_An_Encoder_Or_A_Markdig_Pipeline()
    {
        var producers = FrontFiles()
            .Where(file => Regex.IsMatch(file.Text, @"\(MarkupString\)"))
            .ToList();

        Assert.True(producers.Count >= 5,
            $"Only {producers.Count} MarkupString sites found; the scan is not seeing the app.");

        var offenders = producers
            .Where(file => !SafeSources.Any(marker => Companion(file).Contains(marker, StringComparison.Ordinal)))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These render a MarkupString whose HTML is neither encoded nor produced by a Markdig "
            + $"pipeline with DisableHtml(): {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// A .razor and its .razor.cs are one component: the markup renders what the code-behind built,
    /// so the safety marker may live in either half.
    /// </summary>
    private static string Companion((string Name, string Path, string Text) file)
    {
        var sibling = file.Path.EndsWith(".razor", StringComparison.Ordinal)
            ? file.Path + ".cs"
            : file.Path[..^3];
        return file.Text + (File.Exists(sibling) ? File.ReadAllText(sibling) : string.Empty);
    }

    private static IEnumerable<(string Name, string Path, string Text)> FrontFiles()
    {
        var root = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        return RepositoryScan.Enumerate(root, "*.razor")
            .Concat(RepositoryScan.Enumerate(root, "*.cs"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Name: Path.GetFileName(path), Path: path, Text: File.ReadAllText(path)));
    }
}
