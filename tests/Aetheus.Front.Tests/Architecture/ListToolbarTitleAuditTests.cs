// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>The page header owns the page title; shared list toolbars must not duplicate
/// it.</summary>
public sealed class ListToolbarTitleAuditTests
{
    private static readonly Regex Toolbar = new(
        """<div class="pipeline-list-toolbar">(?<body>.*?)</Omni(?:Stack|Row)>""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Heading = new(
        """TextStyle="TextStyle\.H[1-6]"|<h[1-6][\s>]|<OmniHeading\b""",
        RegexOptions.CultureInvariant);

    [Fact]
    public void ListToolbars_CarryNoTitleOfTheirOwn()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var toolbars = 0;
        var violations = new List<string>();
        foreach (var file in RepositoryScan.Enumerate(front, "*.razor"))
        {
            foreach (Match toolbar in Toolbar.Matches(File.ReadAllText(file)))
            {
                toolbars++;
                if (Heading.IsMatch(toolbar.Groups["body"].Value))
                    violations.Add(Path.GetRelativePath(front, file));
            }
        }

        // The count confirms that the remaining Git-repository toolbar is detected. The template list's own
        // toolbar was a spacer and its actions; they moved to the page header (R-109) and the toolbar is gone.
        Assert.True(toolbars >= 1, $"Expected the shared list toolbars to be found, got {toolbars}.");
        Assert.True(violations.Count == 0,
            "A list toolbar repeats the page title that OmniPageHeader already renders:\n"
            + string.Join("\n", violations));
    }
}
