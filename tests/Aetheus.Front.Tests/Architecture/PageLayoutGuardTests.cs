// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette 2026-10-25, layout lines R-46, R-50, R-51, R-53 and R-60. These guards read the sources
/// (<c>app.css</c> and the shell markup): they prove the rules exist and cannot be dropped silently, not
/// the pixels. The pixels (gutter alignment, centring, bars, row counts) are measured in the browser, see the
/// list of measures in the delivery report.
/// </summary>
public sealed class PageLayoutGuardTests
{
    private sealed record Rule(string Selector, string Body, string? Media);

    private static readonly string CssSource = File.ReadAllText(
        Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

    private static readonly string Css = Regex.Replace(CssSource, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static readonly IReadOnlyList<Rule> Rules = Parse(Css, media: null);

    private static List<Rule> Parse(string text, string? media)
    {
        var rules = new List<Rule>();
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('{', index);
            if (open < 0) break;
            var prelude = text[index..open].Trim();
            var close = MatchingBrace(text, open);
            var inner = text[(open + 1)..close];
            if (prelude.StartsWith("@media", StringComparison.Ordinal))
                rules.AddRange(Parse(inner, prelude));
            else if (!prelude.StartsWith('@'))
                rules.Add(new Rule(Regex.Replace(prelude, @"\s+", " "), Regex.Replace(inner, @"\s+", " ").Trim(), media));
            index = close + 1;
        }

        return rules;
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}' && --depth == 0) return index;
        }

        throw new InvalidOperationException("Unbalanced braces in app.css.");
    }

    private static IEnumerable<Rule> WithSelector(string exactSelector) =>
        Rules.Where(rule => rule.Selector.Split(',').Select(part => part.Trim()).Contains(exactSelector, StringComparer.Ordinal));

    [Fact]
    public void R46_TheBodyWrapperAddsNoHorizontalGutterOfItsOwn()
    {
        var layout = File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Layout", "MainLayout.razor"));
        var wrapper = Regex.Match(layout, @"class=""([^""]*\bapp-body-wrapper\b[^""]*)""");
        Assert.True(wrapper.Success, "The body wrapper moved: the guard would protect nothing.");
        Assert.DoesNotMatch(@"\bomni-u-px-", wrapper.Groups[1].Value);

        var horizontalPadding = Rules.Where(rule => rule.Selector.Contains("app-body-wrapper", StringComparison.Ordinal)
                && Regex.IsMatch(rule.Body, @"padding(-inline|-left|-right)(-start|-end)?\s*:"))
            .Select(rule => rule.Selector);
        Assert.Empty(horizontalPadding);
    }

    [Fact]
    public void R46_OmniMainSuppliesTheOneGutter_EqualToTheHeaderPadding()
    {
        var gutter = Rules.Where(rule => rule.Selector == ":root")
            .Select(rule => Regex.Match(rule.Body, @"--aetheus-page-gutter\s*:\s*([^;]+);"))
            .FirstOrDefault(match => match.Success);
        Assert.True(gutter is not null, "--aetheus-page-gutter is not defined on :root.");
        // OE's header side padding (--omni-header-pad-x), the edge the content lines up with.
        Assert.Equal("0.75rem", gutter.Groups[1].Value.Trim());

        var main = Assert.Single(WithSelector(".omni-main.omni-main--scrollable"));
        Assert.Null(main.Media);
        Assert.Matches(@"padding-inline\s*:\s*max\(var\(--aetheus-page-gutter\)\s*,\s*calc\(\(100% - var\(--omni-main-max-width\)\) / 2 \+ var\(--aetheus-page-gutter\)\)\)", main.Body);
    }

    [Fact]
    public void R46_TheHeaderGivesBackTheWidthOfThePageScrollbar_SoItsRightEdgeIsTheContents()
    {
        var header = Assert.Single(WithSelector(".omni-layout > .omni-header"));
        Assert.Null(header.Media);
        Assert.Matches(@"padding-inline-end\s*:\s*calc\(var\(--omni-header-pad-x\)\s*\+\s*var\(--aetheus-scrollbar-width\s*,\s*0px\)\)", header.Body);

        // The variable is published by layout.js from the real scrollbar of #main-content (tests/js/layout.test.cjs).
        var script = File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "js", "layout.js"));
        Assert.Contains("--aetheus-scrollbar-width", script, StringComparison.Ordinal);
    }

    [Fact]
    public void R14_TheRunTabsPanelIsSpacedFromTheRailByTheStandardGap()
    {
        // The page stack separates its blocks by --omni-space-md; OE's panel padding is --omni-space-lg, which made
        // the tab content sit further from the rail than the blocks sit from each other.
        var panel = Assert.Single(WithSelector(".pipeline-run-tabs > .omni-tabs__panel"));
        Assert.Null(panel.Media);
        Assert.Matches(@"padding-block\s*:\s*var\(--omni-space-md\)", panel.Body);
    }

    [Fact]
    public void R39_TheRunTabRailIsStickyOnlyBehindTheOptInModifier_AndNoMarkupSetsIt()
    {
        // The rail used to stick with the page background showing behind it. The rule is kept for the user to
        // bring back later, but only under .pipeline-run-tabs--sticky-strip.
        Assert.Empty(WithSelector(".pipeline-run-tabs > .omni-tabs__strip"));
        var sticky = Assert.Single(WithSelector(".pipeline-run-tabs.pipeline-run-tabs--sticky-strip > .omni-tabs__strip"));
        Assert.Matches(@"position\s*:\s*sticky", sticky.Body);

        var page = File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Components", "Pipelines", "PipelineRun.razor"));
        Assert.DoesNotContain("pipeline-run-tabs--sticky-strip", page, StringComparison.Ordinal);
    }

    [Fact]
    public void R50_TheFoldedAlertEntryIsCentredOnTheIconAxis()
    {
        var link = Assert.Single(WithSelector(".omni-panel-menu--icons .nav-alert-item .omni-panel-menu__link"));
        Assert.Matches(@"padding-inline\s*:\s*var\(--omni-sidebar-inset\)", link.Body);

        var pill = Assert.Single(WithSelector(".omni-panel-menu--icons .nav-alert-item > .nav-alert-count"));
        Assert.Matches(@"position\s*:\s*static", pill.Body);
        Assert.Matches(@"align-self\s*:\s*center", pill.Body);
        Assert.DoesNotMatch(@"\b(right|left)\s*:", pill.Body);
    }

    [Fact]
    public void R51_TheScrollBodyIsTheOnlyHorizontalScrollerOfAGrid()
    {
        var viewport = Assert.Single(WithSelector(".omni-data-grid .omni-data-grid__viewport"));
        Assert.Matches(@"overflow-x\s*:\s*auto", viewport.Body);

        var grids = WithSelector(".omni-data-grid").ToList();
        Assert.NotEmpty(grids);
        var scrollers = grids.Where(rule => Regex.IsMatch(rule.Body, @"overflow(-x)?\s*:\s*(auto|scroll|overlay)"))
            .Select(rule => $"{rule.Media} {rule.Selector} {{ {rule.Body} }}");
        Assert.Empty(scrollers);
    }

    [Fact]
    public void R53_EveryGridBodyFloorReadsTheOneSharedTenRowMinimum()
    {
        Assert.Matches(@"--aetheus-grid-min-rows\s*:\s*10\s*;", Css);
        Assert.Matches(
            @"--aetheus-grid-min-viewport\s*:\s*calc\(\(var\(--aetheus-grid-min-rows\)\s*\+\s*1\)\s*\*\s*var\(--aetheus-grid-row-height\)\)\s*;",
            Css);

        var floors = Rules.Where(rule => rule.Selector.Contains("omni-data-grid__viewport", StringComparison.Ordinal)
                && Regex.IsMatch(rule.Body, @"min-(height|block-size)\s*:")).ToList();
        // OE's own fill grids, the Aetheus full-height wrapper, its virtualized variant and both together.
        Assert.True(floors.Count >= 4, $"Expected the four shared floors, found {floors.Count}.");
        var own = floors.Where(rule => !rule.Body.Contains("var(--aetheus-grid-min-viewport)", StringComparison.Ordinal))
            .Select(rule => rule.Selector);
        Assert.Empty(own);

        var oeFill = Assert.Single(WithSelector(".omni-data-grid--fill .omni-data-grid__viewport--fill"));
        Assert.Matches(@"min-height\s*:\s*max\(var\(--aetheus-grid-min-viewport\)\s*,\s*min\(var\(--omni-grid-viewport-min,\s*0px\)\s*,\s*var\(--omni-grid-viewport-room,\s*var\(--omni-grid-viewport-min,\s*0px\)\)\)\)", oeFill.Body);
    }

    [Fact]
    public void R121_NoGridBodyFloorGoesBeyondTheRoomTheGridHas()
    {
        // OE's MinHeight (75dvh) is a floor of the scroll body; on a grid that starts low on the page it was larger
        // than the room OE measured, so the page and the grid both scrolled (measured on the Certbot page: grid at
        // 366 px, 796 px of room, 907 px of floor). Every Aetheus floor is capped by the room OE (1.9.4) measures for
        // the scroll body, --omni-grid-viewport-room, down to the ten rows of R-53; the Aetheus estimate is gone.
        Assert.DoesNotContain("--aetheus-grid-room", Css, StringComparison.Ordinal);

        var floors = Rules.Where(rule => rule.Selector.Contains("omni-data-grid__viewport", StringComparison.Ordinal)
                && Regex.IsMatch(rule.Body, @"min-(height|block-size)\s*:")).ToList();
        Assert.NotEmpty(floors);
        var uncapped = floors.Where(rule => !rule.Body.Contains("var(--omni-grid-viewport-room", StringComparison.Ordinal))
            .Select(rule => rule.Selector);
        Assert.Empty(uncapped);
    }

    [Fact]
    public void R60_ADialogHeightIsAlwaysCappedByTheDynamicViewport()
    {
        var heights = Rules.Where(rule => rule.Selector.Contains(".omni-dialog:has(.aetheus-dialog--height-", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, heights.Count);
        foreach (var rule in heights)
        {
            // vh is the viewport with a phone's address bar hidden: it goes past the screen really visible.
            Assert.DoesNotMatch(@"(?<![a-z])\d*\.?\d+vh\b", rule.Body);
            Assert.Contains("calc(100dvh - 2rem)", rule.Body, StringComparison.Ordinal);
        }
    }
}
