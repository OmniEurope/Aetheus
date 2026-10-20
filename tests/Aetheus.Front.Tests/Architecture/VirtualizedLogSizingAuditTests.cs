// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

public class VirtualizedLogSizingAuditTests
{
    [Fact]
    public void VirtualizedLogRows_MatchTheirDeclaredItemSize()
    {
        var root = FindRepoRoot();
        var markup = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Front", "Components", "Pipelines", "PipelineRun.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        var itemSize = Regex.Match(markup, @"<AetheusVirtualList\b[^>]*\bItemSize=""(?<size>\d+)""")
            .Groups["size"].Value;
        var rowRule = Regex.Match(css, @"\.run-log-line\s*\{(?<body>.*?)\}", RegexOptions.Singleline)
            .Groups["body"].Value;

        Assert.Equal("21", itemSize);
        Assert.Contains("height: 1.3125rem", rowRule);
        Assert.Contains("white-space: pre", rowRule);
        Assert.DoesNotContain("pre-wrap", rowRule);
    }

    [Fact]
    public void RunTimeline_WithManySteps_ScrollsInItsPanelInsteadOfClippingTheTree()
    {
        var root = FindRepoRoot();
        var css = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));
        // The side-by-side rule begins at the line start; the indented phone rule stacks the
        // timeline and returns scrolling to the page.
        var timelineMatch = Regex.Match(
            css,
            @"^\.run-split-timeline\s*\{(?<body>.*?)\}",
            RegexOptions.Singleline | RegexOptions.Multiline);
        var timelineRule = timelineMatch.Groups["body"].Value;
        var stackedMatch = Regex.Match(
            css,
            @"@media \(max-width: 39\.99rem\)\s*\{[^@]*?\n\s+\.run-split-timeline\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);
        var stackedTimelineRule = stackedMatch.Groups["body"].Value;
        // Same specificity: the phone rule only wins when it comes after the side-by-side one. Placed
        // before it, it compiled and never applied (found in the browser on 2026-09-28).
        Assert.True(stackedMatch.Success && timelineMatch.Success && stackedMatch.Index > timelineMatch.Index,
            "The stacked .run-split-timeline rule must come after the side-by-side rule.");
        var treeRule = Regex.Match(
            css,
            @"\.run-tree:not\(\.run-tree-nested\)\s*\{(?<body>.*?)\}",
            RegexOptions.Singleline).Groups["body"].Value;

        Assert.Contains("min-height: 0", timelineRule);
        Assert.Contains("overflow-y: auto", timelineRule);
        Assert.Contains("overscroll-behavior: contain", timelineRule);
        Assert.Contains("scrollbar-gutter: stable", timelineRule);
        Assert.Contains("flex: 0 0 auto", treeRule);
        Assert.Contains("overflow-y: visible", stackedTimelineRule);
        Assert.Contains("overscroll-behavior: auto", stackedTimelineRule);
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
