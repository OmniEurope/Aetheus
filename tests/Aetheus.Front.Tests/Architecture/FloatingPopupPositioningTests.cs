// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// A popup built on a library card carries the library's own class as well as its own, and the theme
/// declares <c>position: relative</c> on that class at exactly the same specificity. Whichever
/// stylesheet the browser applied last then wins, and when the theme wins the popup falls back into the
/// document flow. That is not a cosmetic difference: the server header is a horizontal flex with centred
/// items, so an in-flow popup grew the row to its own height and pushed the title, the badges and the
/// buttons down the page every time the three-dot menu was opened.
///
/// Every absolutely positioned popup rule must therefore be selected through its wrapper, which is two
/// classes and wins whatever the cascade order.
/// </summary>
public class FloatingPopupPositioningTests
{
    private static readonly string[] PopupClasses =
    [
        "column-chooser-popup"
    ];

    [Fact]
    public void EveryPopupRule_IsScopedUnderItsWrapper()
    {
        var css = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        var unscoped = PopupClasses
            .Where(popup => Regex.IsMatch(
                css,
                $@"(?m)^\s*\.{Regex.Escape(popup)}\s*\{{",
                RegexOptions.CultureInvariant))
            .ToList();

        Assert.True(
            unscoped.Count == 0,
            "These popup rules select on their own class alone, so .rz-card can tie and win, dropping the "
            + "popup back into the flow. Select through the wrapper instead (.wrapper > .popup): "
            + string.Join(", ", unscoped));
    }

    [Theory]
    [InlineData("column-chooser-wrap", "column-chooser-popup")]
    public void EveryPopup_IsStillPositionedAbsolutely(string wrapper, string popup)
    {
        // The scoping above is worthless if the declaration itself goes missing, so both halves are
        // pinned: the selector shape, and the property that takes the popup out of the flow.
        var css = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        var rule = Regex.Match(
            css,
            $@"\.{Regex.Escape(wrapper)}\s*>\s*\.{Regex.Escape(popup)}\s*\{{(?<body>[^}}]*)\}}",
            RegexOptions.CultureInvariant);

        Assert.True(rule.Success, $"No wrapper-scoped rule found for .{popup}.");
        Assert.Contains("position: absolute", rule.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// <summary>OE's overflow-menu script owns viewport-clamped positioning. Application CSS must
    /// not override those coordinates or require CSS anchor positioning.</summary>
    [Fact]
    public void RunActionsMenu_IsTheOeOverflowMenu_AndAppCssNeverMovesItsPopup()
    {
        var header = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "Components", "Pipelines", "PipelineRunHeader.razor"));
        // The run's "..." menu is the page header's own (OmniPageHeader.MenuContent), which OE
        // draws as its OmniOverflowMenu.
        Assert.Contains("<MenuContent>", header, StringComparison.Ordinal);
        Assert.DoesNotContain("OmniPopover", header, StringComparison.Ordinal);

        var css = Regex.Replace(File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var offenders = Regex.Matches(css, @"(?<selector>[^{}]*omni-overflow-menu__popup[^{}]*)\{(?<body>[^}]*)\}")
            .Where(rule => Regex.IsMatch(rule.Groups["body"].Value,
                @"(?<![-\w])(position|left|right|top|bottom|inset|inset-inline|inset-block|transform|translate|margin|margin-inline|margin-left|margin-right|position-anchor|position-try|position-try-fallbacks)\s*:"))
            .Select(rule => rule.Groups["selector"].Value.Trim())
            .ToList();

        Assert.True(offenders.Count == 0,
            "OE places the overflow menu from script, clamped inside the window; these app.css rules move it: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void PageHeaderFrame_DoesNotClipItsOverflowingPopup()
    {
        // The three-dot menu hangs below the header actions, outside the header's fixed height. With
        // overflow hidden on the frame the menu was rendered and then cut off entirely: it never
        // showed (measured 2026-09-21 on /servers/4/projects, popup box 160..369px inside an 84px frame).
        var css = Regex.Replace(File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        // The page-header frame must not clip overflowing menus.
        var frames = Regex.Matches(css, @"(?<selector>[^{}]*omni-page-header__(?:frame|row)[^{}]*)\{(?<body>[^}]*)\}")
            .Select(rule => rule.Groups["body"].Value)
            .ToList();

        Assert.NotEmpty(frames);
        Assert.All(frames, body => Assert.DoesNotMatch(@"overflow(-[xy])?:\s*(hidden|clip|auto|scroll)", body));
    }
}
