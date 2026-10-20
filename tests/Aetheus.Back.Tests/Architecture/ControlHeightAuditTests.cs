// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Controls outside tables share the control-height token; guards reject per-call size
/// overrides and literal control heights. Grid actions use their row-specific size.</summary>
public sealed class ControlHeightAuditTests
{
    private const string Token = "var(--aetheus-control-height)";

    /// <summary>
    /// Selectors that name a control rather than a layout box. A block whose selector list touches
    /// one of these may not carry a literal height.
    /// </summary>
    private static readonly string[] ControlSelectors =
    [
        ".omni-button", ".omni-badge", ".omni-text-box", ".omni-input", ".omni-combo", ".header-action-btn",
    ];

    /// <summary>
    /// Written exceptions, each with its reason. Anything else must use the token.
    /// </summary>
    private static readonly string[] ExemptSelectors =
    [
        // A row action is governed by the grid row height, not by the page control token.
        ".omni-data-grid", ".aetheus-grid",
        // The grade letter follows the text line it annotates (lots 7 and 18).
        ".grade-badge",
    ];

    [Fact]
    public void No_Call_Site_Sets_Its_Own_Button_Size()
    {
        var files = FrontRazorFiles();
        Assert.True(files.Count >= 100, $"Only {files.Count} razor files scanned; the guard is not seeing the app.");

        var offenders = files
            .Where(file => file.Text.Contains("Size=\"ButtonSize.", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A per-call-site ButtonSize is a second control height. Remove it and let "
            + $"{Token} decide: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void No_Control_Selector_Carries_A_Literal_Height()
    {
        var css = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        var blocks = Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.Singleline)
            .Select(match => (Selector: match.Groups["selector"].Value, Body: match.Groups["body"].Value))
            .Where(block => ControlSelectors.Any(selector =>
                Regex.IsMatch(block.Selector, Regex.Escape(selector) + @"(?![\w-])")))
            .Where(block => !ExemptSelectors.Any(exempt =>
                block.Selector.Contains(exempt, StringComparison.Ordinal)))
            .ToList();

        Assert.True(blocks.Count >= 5, $"Only {blocks.Count} control blocks found; the scan is not reading app.css.");

        var offenders = blocks
            .Where(block => Regex.IsMatch(block.Body, @"(?<!-)\b(?:min-|max-)?height:\s*[0-9.]+(?:rem|px)"))
            .Select(block => block.Selector.Trim().ReplaceLineEndings(" "))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These write a control height instead of using {Token}: {string.Join(" | ", offenders)}");
    }

    /// <summary>
    /// The maintained OE controls use the shared token as their height rather than inheriting the
    /// package's larger default geometry.
    /// </summary>
    [Fact]
    public void Omni_Controls_Use_The_Shared_Height()
    {
        var css = Regex.Replace(File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        string Body(string selector) =>
            string.Join("\n", Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.Singleline)
                .Where(match => match.Groups["selector"].Value.Split(',').Any(part => part.Trim() == selector))
                .Select(match => match.Groups["body"].Value));

        foreach (var selector in new[] { ".omni-button", ".omni-text-box", ".omni-input", ".omni-combo" })
        {
            Assert.Contains($"min-height: {Token};", Body(selector), StringComparison.Ordinal);
            Assert.Contains($"height: {Token};", Body(selector), StringComparison.Ordinal);
        }
    }

    /// <summary>OE owns fixed page-header geometry; application CSS may size title text but not the
    /// frame, except to expand an opened compact header.</summary>
    [Fact]
    public void The_Page_Header_Frame_Has_One_Height()
    {
        var css = Regex.Replace(File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var rules = Regex.Matches(css, @"(?<selector>[^{}]*omni-page-header__(?:frame|title|row)[^{}]*)\{(?<body>[^}]*)\}").ToList();
        Assert.NotEmpty(rules);

        var offenders = rules
            .Where(rule => Regex.IsMatch(rule.Groups["body"].Value,
                @"(?<![-\w])(height|block-size|min-height|max-height|min-block-size|max-block-size|line-height)\s*:"))
            .Where(rule => !rule.Groups["selector"].Value.Contains("omni-page-header__details--open", StringComparison.Ordinal))
            .Select(rule => rule.Groups["selector"].Value.Trim())
            .ToList();

        Assert.True(offenders.Count == 0,
            "These app.css rules change the page header's fixed geometry: " + string.Join(", ", offenders));
    }

    private static List<(string Name, string Text)> FrontRazorFiles() =>
        RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front"), "*.razor")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .ToList();
}
