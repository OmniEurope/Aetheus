// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>The shared radius token keeps site corners consistent.</summary>
public sealed class BorderRadiusAuditTests
{
    /// <summary>Deliberately round shapes, each named for why it is not a box.</summary>
    private static readonly string[] RoundShapeSelectors =
    [
        ".dev-env-chip",          // a pill in the top bar
        ".login-logo-img",        // the logo artwork
        ".monaco-editor",         // the embedded editor's own scrollbar thumb
        ".heartbeat-bucket",      // a chart mark a few pixels wide, not a box: 4px would make it a pill
    ];

    private static readonly Regex Block = new(@"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.Singleline);

    private static readonly Regex RadiusDeclaration = new(
        @"(?:border|border-(?:top|bottom)-(?:left|right)|border-(?:start|end)-(?:start|end))-radius:\s*(?<value>[^;}]+)");

    [Fact]
    public void Every_Corner_Uses_The_Radius_Token()
    {
        var css = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        Assert.Contains("--aetheus-radius:", css, StringComparison.Ordinal);

        var declarations = Block.Matches(css)
            .Where(block => !RoundShapeSelectors.Any(selector =>
                block.Groups["selector"].Value.Contains(selector, StringComparison.Ordinal)))
            .SelectMany(block => RadiusDeclaration.Matches(block.Groups["body"].Value)
                .Select(match => (Selector: block.Groups["selector"].Value.Trim(), Value: match.Groups["value"].Value.Trim())))
            .ToList();

        Assert.True(declarations.Count >= 50, $"Only {declarations.Count} radius declarations read; the scan is not seeing app.css.");

        var offenders = declarations
            .Where(declaration => Regex.IsMatch(declaration.Value, @"\d(?:rem|px)\b")
                // A pill (999rem / 999px) is round by construction; a var() fallback and a calc()
                // derived from a token are not a second radius.
                && !declaration.Value.Contains("999", StringComparison.Ordinal)
                && !declaration.Value.StartsWith("var(", StringComparison.Ordinal)
                && !declaration.Value.StartsWith("calc(", StringComparison.Ordinal))
            .Select(declaration => $"{declaration.Selector.ReplaceLineEndings(" ")}: {declaration.Value}")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These write their own corner radius instead of var(--aetheus-radius): {string.Join(" | ", offenders)}");
    }
}
