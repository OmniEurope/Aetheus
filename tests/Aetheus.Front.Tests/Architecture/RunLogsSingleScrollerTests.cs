// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Recette R1-31: on a phone the logs of a step showed two horizontal scrollbars. The log
/// terminal is the one horizontal scroller, and the stacked single column can never be widened by the
/// timeline above it (a grid track of 1fr is minmax(auto, 1fr)). A stylesheet contract: the layout
/// itself is not measured by bUnit.</summary>
public class RunLogsSingleScrollerTests
{
    private static string Css() => File.ReadAllText(Path.Combine(
        RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, Regex.Escape(selector) + @"\s*\{([^}]*)\}");
        Assert.True(match.Success, $"Rule not found: {selector}");
        return match.Groups[1].Value;
    }

    [Fact]
    public void TheLogTerminal_IsTheOneHorizontalScroller()
    {
        var terminal = Rule(Css(), ".run-logs-terminal");

        Assert.Contains("overflow-x: auto", terminal, StringComparison.Ordinal);
        Assert.Contains("min-width: 0", terminal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStackedMobileLayout_CannotBeWidenedByItsTimeline()
    {
        var css = Css();
        var mobile = Regex.Match(css, @"@media \(max-width: 39\.99rem\) \{\s*\.run-timeline-rail[\s\S]*?\.run-split-logs \{[^}]*\}\s*\}");

        Assert.True(mobile.Success, "The mobile run layout block was not found.");
        Assert.Contains("grid-template-columns: minmax(0, 1fr) !important", mobile.Value, StringComparison.Ordinal);
        Assert.Contains("min-width: 0", Rule(css, ".run-split-timeline"), StringComparison.Ordinal);
    }
}
