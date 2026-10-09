// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// The Secondary style is banned on buttons (ButtonStyle.Secondary) and on badges (OmniTone.Secondary):
/// it rendered nearly identically to Light and had split 117/224 across the app for no expressible
/// reason (decision of 2026-08-19; ui-patterns: "Secondary is banned on badges too"). ButtonStyle is
/// the retired library's name; the OE button's neutral grey is OmniButtonVariant.Secondary, which stays
/// allowed. OmniTone.Accent is NOT banned: counts use it, and R2-058 maps a running service to it
/// (FRONTTEST-001: this summary and the badge diagnostic used to name Accent while the scan matched
/// Secondary).
///
/// Scope note: this guard checks ONLY that ban. Button colours follow STD-BTN (revised 2026-09-28,
/// colour follows importance: Primary the main action of its zone, Secondary the others, Ghost a minor
/// one, Danger a destructive one, never Success), enforced by ButtonRoleColourGuardTests and
/// ButtonFamilyAndGridNavigationAuditTests; see docs/contracts/ui-patterns.md.
/// </summary>
public class ButtonSemanticsAuditTests
{
    private static readonly Regex BannedButtonStyle = new(@"\bButtonStyle\.Secondary\b", RegexOptions.Compiled);
    private static readonly Regex BannedBadgeTone = new(@"\bOmniTone\.Secondary\b", RegexOptions.Compiled);

    [Theory]
    [InlineData("<OmniBadge Tone=\"OmniTone.Secondary\">x</OmniBadge>", 1)]
    [InlineData("Tone=\"@(ok ? OmniTone.Secondary : OmniTone.Danger)\" Variant=\"OmniTone.Secondary\"", 2)]
    [InlineData("<OmniBadge Tone=\"OmniTone.Accent\">x</OmniBadge>", 0)]
    [InlineData("<CountBadge Variant=\"OmniTone.Neutral\" />", 0)]
    [InlineData("OmniTone.SecondaryText", 0)]
    public void BadgeScan_FlagsTheSecondaryToneOnly(string source, int expected)
    {
        Assert.Equal(expected, BannedBadgeTone.Matches(source).Count);
    }

    [Theory]
    [InlineData("ButtonStyle=\"ButtonStyle.Secondary\"", 1)]
    [InlineData("Variant=\"OmniButtonVariant.Secondary\"", 0)]
    [InlineData("ButtonStyle=\"ButtonStyle.Light\"", 0)]
    public void ButtonScan_FlagsTheRetiredSecondaryStyleOnly(string source, int expected)
    {
        Assert.Equal(expected, BannedButtonStyle.Matches(source).Count);
    }

    [Fact]
    public void NoButton_UsesSecondaryStyle()
    {
        var offenders = FrontFiles()
            .Select(f => (Rel: Rel(f), Count: BannedButtonStyle.Matches(File.ReadAllText(f)).Count))
            .Where(x => x.Count > 0)
            .ToList();

        Assert.True(offenders.Count == 0,
            "ButtonStyle.Secondary is banned - use ButtonStyle.Light (docs/contracts/ui-patterns.md):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => $"  - {o.Rel} ({o.Count})")));
    }

    [Fact]
    public void NoBadge_UsesSecondaryStyle()
    {
        var offenders = FrontFiles()
            .Select(f => (Rel: Rel(f), Count: BannedBadgeTone.Matches(File.ReadAllText(f)).Count))
            .Where(x => x.Count > 0)
            .ToList();

        Assert.True(offenders.Count == 0,
            "OmniTone.Secondary is banned on badges - use OmniTone.Neutral (docs/contracts/ui-patterns.md):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => $"  - {o.Rel} ({o.Count})")));
    }


    private static IEnumerable<string> FrontFiles()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        return RepositoryScan.Enumerate(root, "*.*")
            .Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string Rel(string fullPath)
    {
        var root = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
