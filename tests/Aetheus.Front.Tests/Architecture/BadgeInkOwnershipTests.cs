// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>R-22: a solid badge's fill and ink belong to OE (one ink per mode, held at 4.5:1 by its
/// contrast matrix). The app stylesheet sizes badges but never recolours a tone.</summary>
public class BadgeInkOwnershipTests
{
    private static readonly Regex Comment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Rule = new(
        @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}",
        RegexOptions.CultureInvariant);

    private static readonly Regex Paint = new(
        @"(?<![-\w])(color|background(?:-color)?)\s*:",
        RegexOptions.CultureInvariant);

    [Fact]
    public void NoAppRule_RecoloursABadgeTone()
    {
        var css = Comment.Replace(
            File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")),
            string.Empty);

        var rules = Rule.Matches(css).ToList();
        Assert.Contains(rules, rule => rule.Groups["selector"].Value.Contains(".omni-badge", StringComparison.Ordinal));

        var violations = rules
            .Where(rule => rule.Groups["selector"].Value.Contains("omni-badge--", StringComparison.Ordinal)
                || rule.Groups["selector"].Value.Contains("omni-alert--solid", StringComparison.Ordinal))
            // The registry package type badges keep the brand colour of NuGet, npm and Maven: a decision of
            // the user (R1-19), not a workaround of the tone ink.
            .Where(rule => !rule.Groups["selector"].Value.Contains("package-kind-badge--", StringComparison.Ordinal))
            // The A to F quality grade ladder is a rating scale with its own palette, not a status tone.
            .Where(rule => !rule.Groups["selector"].Value.Contains("grade-badge--", StringComparison.Ordinal))
            .Where(rule => Paint.IsMatch(rule.Groups["body"].Value))
            .Select(rule => rule.Groups["selector"].Value.Trim())
            .ToList();

        Assert.True(
            violations.Count == 0,
            "These rules recolour an OE badge tone; its fill and ink come from OE's --omni-badge-solid-* tokens:\n  "
            + string.Join("\n  ", violations));
    }
}
