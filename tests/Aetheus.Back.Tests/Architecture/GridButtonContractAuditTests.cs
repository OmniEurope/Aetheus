// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Grid-row actions use icons with title and aria-label text to retain accessible names
/// without consuming labelled-button width.</summary>
public sealed class GridButtonContractAuditTests
{
    /// <summary>
    /// A column template. Matching on the grid column itself is wrong: a self-closing column has
    /// no end tag, so the block ran on to the next grid and swallowed the toolbars in between, which
    /// legitimately carry labelled buttons. A row action lives in a Template; a toolbar button does not.
    /// </summary>
    private static readonly Regex GridButton = new(
        """<Template\b.*?</Template>""", RegexOptions.Singleline);

    private static readonly Regex Button = new("""<OmniButton\b(?:(?!</OmniButton>).)*?</OmniButton>""", RegexOptions.Singleline);

    /// <summary>
    /// The rule is about the ACTION name, not about any text. A localized label ("Edit", "Delete")
    /// repeats what the icon already says and costs a column of width on every row. A data label
    /// (a tag, a service name, a port) is the cell's content rendered as a chip: removing it would
    /// empty the cell, so it stays.
    /// </summary>
    private static readonly Regex LocalizedLabel = new("""<span[^>]*>(?:(?!</span>).)*?L\[""", RegexOptions.Singleline);

    [Fact]
    public void No_Grid_Button_Repeats_Its_Action_Name()
    {
        var offenders = GridButtons()
            .Where(entry => LocalizedLabel.IsMatch(entry.Button)
                && !entry.Button.Contains("OmniButtonType.Submit", StringComparison.Ordinal))
            .Select(entry => entry.File)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These render a labelled action button inside a data row; use title + aria-label: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Every_Grid_Icon_Button_Has_An_Accessible_Name()
    {
        // Only the icon-only ones: a button that shows its own data already names itself on screen.
        var entries = GridButtons()
            .Where(entry => entry.Button.Contains("<OmniIcon", StringComparison.Ordinal)
                && !entry.Button.Contains("<span", StringComparison.Ordinal))
            .ToList();
        Assert.True(entries.Count >= 40, $"Only {entries.Count} grid icon buttons found; the scan is not seeing the app.");

        var offenders = entries
            .Where(entry => !Regex.IsMatch(entry.Button, @"\s(title|aria-label|Label)=", RegexOptions.IgnoreCase))
            .Select(entry => entry.File)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These render an icon-only grid button with no accessible name: {string.Join(", ", offenders)}");
    }

    private static IEnumerable<(string File, string Button)> GridButtons() =>
        RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front"), "*.razor")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => GridButton.Matches(File.ReadAllText(path))
                .SelectMany(column => Button.Matches(column.Value))
                .Select(button => (File: Path.GetFileName(path), Button: button.Value)));
}
