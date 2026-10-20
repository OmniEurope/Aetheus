// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Presentation belongs in app.css, with runtime coordinates applied through CSSOM rather
/// than inline style attributes. The scan distinguishes HTML style attributes from typed component
/// parameters and permits no inline-style exemption.</summary>
public class InlineStyleAuditTests
{
    /// <summary>
    /// Files allowed to set the style attribute, with the reason. Empty on purpose: an entry here
    /// re-introduces a dependency on <c>style-src-attr 'unsafe-inline'</c> in production, so it needs
    /// the policy to be revisited in the same change.
    /// </summary>
    private static readonly Dictionary<string, string> JustifiedInlineStyles =
        new(StringComparer.OrdinalIgnoreCase);

    // The HTML attribute, not the library's typed *Style= parameters: require a non-identifier char before
    // "style", so ButtonStyle=/TextStyle=/OmniTone= do not match.
    private static readonly Regex InlineStyleAttribute =
        new(@"(?<![\w-])[Ss]tyle\s*=\s*""", RegexOptions.Compiled);

    [Fact]
    public void NoRazorFile_UsesAnUnjustifiedInlineStyleAttribute()
    {
        var root = FrontRoot();
        var offenders = new List<string>();

        foreach (var file in RepositoryScan.Enumerate(root, "*.razor"))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;

            var count = InlineStyleAttribute.Matches(File.ReadAllText(file)).Count;
            if (count == 0) continue;

            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!JustifiedInlineStyles.ContainsKey(rel)) offenders.Add($"  - {rel} ({count})");
        }

        Assert.True(offenders.Count == 0,
            "Inline style attributes belong in app.css as a class (docs/contracts/ui-patterns.md). If the value "
            + "is genuinely computed per instance, add the file to JustifiedInlineStyles with the "
            + "reason:" + Environment.NewLine + string.Join(Environment.NewLine, offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void EveryExemption_IsStillUsed()
    {
        var root = FrontRoot();
        var stale = JustifiedInlineStyles.Keys
            .Where(rel =>
            {
                var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(full) || InlineStyleAttribute.Matches(File.ReadAllText(full)).Count == 0;
            })
            .Select(rel => $"  - {rel}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These files no longer use an inline style - drop their exemption so the list keeps meaning "
            + "something:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>Checks that node coordinates travel through data attributes and CSSOM setProperty
    /// rather than setAttribute.</summary>
    [Fact]
    public void NodeCoordinates_AreAppliedThroughTheCssom()
    {
        var front = FrontRoot();
        var markup = File.ReadAllText(Path.Combine(front, "Components", "Pipelines", "StageNode.razor"));
        var script = File.ReadAllText(Path.Combine(front, "wwwroot", "js", "visual-pipeline.js"));

        Assert.Contains("data-node-x=", markup, StringComparison.Ordinal);
        Assert.Contains("data-node-y=", markup, StringComparison.Ordinal);
        Assert.Contains("applyNodePositions", script, StringComparison.Ordinal);
        Assert.Contains("setProperty('--node-x'", script, StringComparison.Ordinal);
        Assert.Contains("setProperty('--node-y'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("setAttribute('style'", script, StringComparison.Ordinal);
    }

    private static string FrontRoot() => Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
