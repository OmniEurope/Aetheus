// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>CountBadge suppresses empty counts structurally; raw count badges are
/// rejected.</summary>
public sealed class ZeroCountBadgeAuditTests
{
    [Fact]
    public void A_Plain_Count_Uses_CountBadge()
    {
        var offenders = FrontRazorFiles()
            .SelectMany(file => Regex
                .Matches(file.Text, @"<OmniBadge[^>]*Text=""@[\w.()]*?(Count|_totalCount|Length)(\(\))?\.ToString\(\)""")
                .Select(_ => file.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These render a raw counter badge that would show \"0\"; use <CountBadge>: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void No_Tile_Moves_Under_The_Pointer()
    {
        // Card hover changes tint and border without moving the targeted text.
        var css = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        var hoverBlocks = Regex.Matches(css, @":hover[^{]*\{[^}]*\}", RegexOptions.Singleline)
            .Select(match => match.Value)
            .Where(block => block.Contains("translateY(-", StringComparison.Ordinal))
            .ToList();

        Assert.True(hoverBlocks.Count == 0,
            $"{hoverBlocks.Count} hover rule(s) still lift their element: {string.Join(" | ", hoverBlocks)}");
    }

    private static IEnumerable<(string Name, string Text)> FrontRazorFiles() =>
        RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front"), "*.razor")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)));
}
