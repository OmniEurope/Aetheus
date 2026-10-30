// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// The badge rule of 2026-10-25 (recette R-44 and R-59), documented in <c>docs/contracts/ui-patterns.md</c>:
/// a badge whose text has no number carries an icon; a badge that carries a number has none (it is a
/// <c>NumberBadge</c>); a one-letter grade is a badge without icon, filled with the colour of its grade
/// (<c>GradeBadge</c>, recette R-59 as the user corrected it, R-107).
/// </summary>
public sealed class BadgeIconRuleGuardTests
{
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>An icon on the badge: the <c>Icon</c> parameter, its <c>&lt;Icon&gt;</c> fragment, or an OmniIcon
    /// drawn inside it.</summary>
    private static readonly Regex IconMarker = new(@"\bIcon=""|<Icon>|<OmniIcon\b", RegexOptions.Compiled);

    /// <summary>What a number badge shows has to look like a number: a count, a formatted value, a version,
    /// a port, a date. A text that merely names something belongs in an OmniBadge with an icon.</summary>
    private static readonly Regex NumberEvidence = new(
        @"Count|Unread|ToString\(|Format\(|\$""|FormatSize|Percent|Version|BuildNumber|Port|Queue|Total|DisplayText|CountText|ExpiresAt|Passed|Failed|Skipped",
        RegexOptions.Compiled);

    private static IEnumerable<(string File, string Tag, string Segment, int Line)> BadgeSegments(string tag)
    {
        var frontRoot = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var open = new Regex($@"<{tag}(?=[\s>])", RegexOptions.Compiled);
        foreach (var file in RepositoryScan.Enumerate(frontRoot, "*.razor"))
        {
            var text = RazorComment.Replace(File.ReadAllText(file), match => new string(' ', match.Length));
            foreach (Match start in open.Matches(text))
            {
                var end = text.IndexOf($"</{tag}>", start.Index, StringComparison.Ordinal);
                var segment = text[start.Index..(end < 0 ? text.Length : end)];
                var line = text.AsSpan(0, start.Index).Count('\n') + 1;
                yield return (Path.GetRelativePath(RepositoryScan.Root, file).Replace('\\', '/'), tag, segment, line);
            }
        }
    }

    [Fact]
    public void EveryTextBadge_CarriesAnIcon()
    {
        var badges = BadgeSegments("OmniBadge").ToList();
        Assert.True(badges.Count >= 150, $"Only {badges.Count} OmniBadge found; the guard is not seeing the app.");

        // NumberBadge and GradeBadge are the two places that draw an icon-less OmniBadge: the badge of a text that
        // carries a number, and the one-letter grade (R-107). EveryNumberBadge_HasNoIcon_AndShowsANumber and
        // TheGradeLetter_IsABadgeWithoutIcon keep them honest.
        var offenders = badges
            .Where(badge => !badge.File.EndsWith("Components/Shared/NumberBadge.razor", StringComparison.Ordinal))
            .Where(badge => !badge.File.EndsWith("Components/Shared/GradeBadge.razor", StringComparison.Ordinal))
            .Where(badge => !IconMarker.IsMatch(badge.Segment))
            .Select(badge => $"{badge.File}:{badge.Line}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A badge whose text has no number carries an icon (recette R-44 and R-59): add Icon=\"@BadgeIcon.For(tone)\" "
            + "(or BadgeIcon.ForState / BadgeIcon.Of(OmniIconName...)), or use <NumberBadge> when the text carries a number:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryNumberBadge_HasNoIcon_AndShowsANumber()
    {
        var badges = BadgeSegments("NumberBadge").ToList();
        Assert.True(badges.Count >= 40, $"Only {badges.Count} NumberBadge found; the guard is not seeing the app.");

        var withIcon = badges.Where(badge => IconMarker.IsMatch(badge.Segment))
            .Select(badge => $"{badge.File}:{badge.Line}").ToList();
        Assert.True(withIcon.Count == 0,
            "A badge that carries a number has no icon (recette R-44): these NumberBadge have one:\n  "
            + string.Join("\n  ", withIcon));

        var notNumbers = badges.Where(badge => !NumberEvidence.IsMatch(badge.Segment))
            .Select(badge => $"{badge.File}:{badge.Line}").ToList();
        Assert.True(notNumbers.Count == 0,
            "A NumberBadge shows a count, a value or a version. A text without number is an OmniBadge with an icon:\n  "
            + string.Join("\n  ", notNumbers));
    }

    /// <summary>Recette R-43 (2026-10-25): a badge takes the size of its content, everywhere; no width floor
    /// (<c>OmniBadgeWidth.Small</c>, <c>Medium</c>, <c>Large</c>) is left on any badge.</summary>
    [Fact]
    public void NoBadge_HasAWidthFloor()
    {
        var frontRoot = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var floor = new Regex(@"OmniBadgeWidth\.(?!Auto\b)\w+", RegexOptions.Compiled);
        var offenders = RepositoryScan.Enumerate(frontRoot, "*.razor")
            .Concat(RepositoryScan.Enumerate(frontRoot, "*.cs"))
            .SelectMany(file => floor.Matches(RazorComment.Replace(File.ReadAllText(file), string.Empty))
                .Select(match => $"{Path.GetRelativePath(RepositoryScan.Root, file).Replace('\\', '/')}: {match.Value}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A badge takes the size of its content (recette R-43): remove these widths:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheNumberBadgeComponent_DrawsNoIcon()
    {
        var component = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Components", "Shared", "NumberBadge.razor");
        var text = RazorComment.Replace(File.ReadAllText(component), string.Empty);

        Assert.DoesNotMatch(IconMarker, text);
    }

    [Fact]
    public void TheGradeLetter_IsABadgeWithoutIcon()
    {
        var component = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Components", "Shared", "GradeBadge.razor");
        var text = RazorComment.Replace(File.ReadAllText(component), string.Empty);

        Assert.Contains("OmniBadge", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(IconMarker, text);
        Assert.DoesNotContain("NumberBadge", text, StringComparison.Ordinal);
        Assert.Contains("grade-badge--", text, StringComparison.Ordinal);
    }
}
