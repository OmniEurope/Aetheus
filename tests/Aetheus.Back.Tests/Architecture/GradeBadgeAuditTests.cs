// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Architecture;

/// <summary>All analysis grades render through GradeBadge and its shared colour mapping.</summary>
public sealed class GradeBadgeAuditTests
{
    /// <summary>Markers of a grade drawn outside the shared component.</summary>
    private static readonly string[] HandRolledGradeMarkers = ["GetGradeBadge", "GradeCss", "project-gate-grade"];

    [Fact]
    public void Only_GradeBadge_Draws_A_Grade()
    {
        var offenders = FrontFiles()
            .Where(file => !file.Name.StartsWith("GradeBadge", StringComparison.Ordinal)
                && !file.Name.Equals("PipelineHelper.cs", StringComparison.Ordinal))
            .Where(file => HandRolledGradeMarkers.Any(marker => file.Text.Contains(marker, StringComparison.Ordinal)))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These files draw a grade themselves instead of using <GradeBadge>: {string.Join(", ", offenders)}");
    }

    /// <summary>Every front source file, with the floor that refuses an empty scan.</summary>
    private static IEnumerable<(string Name, string Text)> FrontFiles()
    {
        var root = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        return RepositoryScan.Enumerate(root, "*.razor")
            .Concat(RepositoryScan.Enumerate(root, "*.cs"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)));
    }
}
