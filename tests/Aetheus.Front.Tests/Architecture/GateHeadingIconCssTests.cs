// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R-27: the blue "Quality" button of the run's Gate tab showed no icon. The rule that colours the card's
/// own heading icon with the accent was written for every <c>.omni-icon</c> under the heading, so it also reached
/// the icon inside the primary (accent) button of the same row: accent on accent. It must reach the heading's
/// direct child only.
/// </summary>
public sealed class GateHeadingIconCssTests
{
    private static readonly string Css = Regex.Replace(
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")),
        @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    [Fact]
    public void TheAccentOfTheHeadingIcon_DoesNotReachTheIconOfTheButtonsOfTheSameRow()
    {
        // Every rule that sets a colour on an icon below the gate heading.
        var rules = Regex.Matches(Css, @"(?<selector>[^{}]*\.analysis-run-gate-heading[^{}]*\.omni-icon[^{}]*)\{(?<body>[^{}]*)\}");
        var colouring = rules
            .Where(rule => rule.Groups["body"].Value.Contains("color:", StringComparison.Ordinal))
            .Select(rule => rule.Groups["selector"].Value.Trim())
            .ToList();

        Assert.NotEmpty(colouring);
        // A descendant combinator (a space before the icon) also reaches the icon inside a button.
        Assert.All(colouring, selector =>
            Assert.DoesNotMatch(@"\.analysis-run-gate-heading\s+\.omni-icon", selector));
    }
}
