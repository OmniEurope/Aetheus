// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

public sealed class ProjectCardHoverContractTests
{
    [Fact]
    public void ProjectCards_HighlightOnlyTheirBorderOnHover()
    {
        var css = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        var cardHoverRules = Regex.Matches(
                css,
                @"\.project-card:hover\s*\{(?<body>[^}]*)\}",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        Assert.NotEmpty(cardHoverRules);
        Assert.All(cardHoverRules, body =>
        {
            Assert.Contains("border-color", body, StringComparison.Ordinal);
            Assert.DoesNotContain("transform", body, StringComparison.Ordinal);
            Assert.DoesNotContain("box-shadow", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ProjectCardName_DoesNotChangeColorOrDecorationOnHover()
    {
        var css = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Front", "wwwroot", "css", "app.css"));
        var match = Regex.Match(
            css,
            @"\.project-card-name:hover\s*\{(?<body>[^}]*)\}",
            RegexOptions.CultureInvariant);

        Assert.True(match.Success, "The project-card-name hover rule is missing.");
        // The hover keeps the resting colour: the tile title token, the same as at rest.
        Assert.Contains("color: var(--aetheus-tile-title)", match.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("text-decoration: none", match.Groups["body"].Value, StringComparison.Ordinal);
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
