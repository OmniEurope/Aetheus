// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>A page body that fills the window (a full-height grid, a page-fill-body such as the template update
/// diff) keeps its bottom padding inside the window's height: otherwise the page scrolls by that padding on top of
/// the grid or diff that already scrolls (R-30 page-scroll-with-grid, R-100: 12 px measured on the update page).</summary>
public sealed class PageFillWrapperBoxSizingTests
{
    private static readonly string Css = Regex.Replace(
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")),
        @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    [Theory]
    [InlineData(@".app-body-wrapper:has(> .aetheus-grid-host > .aetheus-grid-fullheight)")]
    [InlineData(@".app-body-wrapper:has(> .page-fill-body)")]
    public void AFillingPageBody_CountsItsPaddingInsideTheWindowHeight(string selector)
    {
        var rule = Regex.Match(Css, Regex.Escape(selector) + @"\s*\{(?<body>[^{}]*)\}");
        Assert.True(rule.Success, $"rule {selector} not found in app.css");
        Assert.Matches(@"box-sizing\s*:\s*border-box", rule.Groups["body"].Value);
    }

    /// <summary>R-28: a page-fill-body hosted in a tab panel (Supervision > Telemetry > Logs) is stretched by nothing.
    /// The page-level rules give its grid a flex basis of 0 and rely on "min-height: min-content", which Firefox does
    /// not honour on an overflow-hidden flex item (the grid was 0px tall there, 517px in Chromium). The tab-panel
    /// rules state the content height instead: basis auto, never shrunk, block size auto (not OE's fill height).</summary>
    [Theory]
    [InlineData(@".omni-tabs__panel .page-fill-body > .aetheus-grid-host > .aetheus-grid-fullheight", @"block-size\s*:\s*auto")]
    [InlineData(@".omni-tabs__panel .page-fill-body > .aetheus-grid-host > .aetheus-grid-fullheight", @"flex\s*:\s*1\s+0\s+auto")]
    [InlineData(@".omni-tabs__panel .page-fill-body > .aetheus-grid-host:has(> .aetheus-grid-fullheight)", @"flex\s*:\s*1\s+0\s+auto")]
    public void AFillingGridInATabPanel_TakesItsContentHeightInEveryBrowser(string selector, string declaration)
    {
        var rules = Regex.Matches(Css, Regex.Escape(selector) + @"\s*[,{](?<rest>[^{}]*\{)?(?<body>[^{}]*)\}");
        Assert.Contains(rules, rule => Regex.IsMatch(rule.Groups["body"].Value, declaration));
    }

    /// <summary>R-28 follow-up: every other full-height grid. Firefox ignores "min-height: min-content" on an
    /// overflow-hidden flex item (OE's .omni-data-grid--fill and the Aetheus floors), so on a short window the grid kept
    /// only the room OE measured and clipped its rows and pager (1400x600: users 312px against 480px in Chromium,
    /// project pipelines 72px against 396px). The floor is stated in terms both engines honour: content height, never
    /// shrunk, at least the measured room.</summary>
    [Theory]
    [InlineData(@".omni-data-grid.omni-data-grid--fill", @"block-size\s*:\s*auto")]
    [InlineData(@".omni-data-grid.omni-data-grid--fill", @"flex\s*:\s*1\s+0\s+auto")]
    [InlineData(@".omni-data-grid.omni-data-grid--fill", @"min-block-size\s*:\s*var\(--omni-grid-fill-height")]
    [InlineData(@".iam-grid-surface", @"overflow\s*:\s*clip")]
    public void AFillingGrid_DoesNotRelyOnAMinContentFloorThatFirefoxIgnores(string selector, string declaration)
    {
        var rules = Regex.Matches(Css, Regex.Escape(selector) + @"\s*[,{](?<rest>[^{}]*\{)?(?<body>[^{}]*)\}");
        Assert.Contains(rules, rule => Regex.IsMatch(rule.Groups["body"].Value, declaration));
    }

    /// <summary>A flex basis of 0 on a grid, its host or the surface around it leaves the content floor to
    /// "min-height: min-content", which Firefox ignores there: such a rule collapses the grid on a short window.
    /// The chain uses "flex: 1 0 auto" (content basis, never shrunk) instead.</summary>
    [Fact]
    public void AGridChainRule_NeverUsesAZeroFlexBasis()
    {
        var offenders = Regex.Matches(Css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .Where(rule => Regex.IsMatch(rule.Groups["selector"].Value, @"aetheus-grid-fullheight|aetheus-grid-host|iam-grid-surface")
                           && Regex.IsMatch(rule.Groups["body"].Value, @"flex\s*:\s*1\s+1\s+0(?!\.|\d|\w)"))
            .Select(rule => rule.Groups["selector"].Value.Trim())
            .ToList();

        Assert.True(offenders.Count == 0, "Grid chain rules with a zero flex basis (use flex: 1 0 auto): " + string.Join(" | ", offenders));
    }
}
