// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// The spacing inside a card never rests on a vertical margin of inline text (recette R-25, R-33,
/// STD-CARDSPACE): a span, strong, em or small ignores <c>margin-top</c>/<c>margin-bottom</c>, so a
/// title given <c>omni-u-mb-sm</c> still touches the table below it. The title is block text
/// (<c>OmniTextElement.Title</c> or <c>Caption</c>, <c>omni-u-block</c>, a paragraph, a heading) or sits in a
/// gapped <c>OmniStack</c>.
/// </summary>
public sealed class InlineMarginGuardTests
{
    private static readonly Regex CssRule = new(@"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.CultureInvariant);

    private static readonly Regex DisplayDeclaration = new(
        @"(?<![\w-])display\s*:\s*(?!inline\s*(?:;|$)|contents)", RegexOptions.CultureInvariant);

    private static readonly Regex ClassName = new(@"\.(?<c>-?[_a-zA-Z][\w-]*)", RegexOptions.CultureInvariant);

    [Fact]
    public void CardContent_NeverAsksAnInlineElementForAVerticalMargin()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var displayClasses = LoadDisplayClasses(front);
        var files = RepositoryScan.Enumerate(front, "*.razor");
        var violations = files.SelectMany(file =>
            BlockSpacingScanner.ScanInlineMargins(File.ReadAllText(file), displayClasses)
                .Select(v => $"{Path.GetRelativePath(front, file)}:{v.Line}  {v.First} in {v.Parent}")).ToList();

        Assert.True(files.Count >= 300, $"Expected the front to hold many pages, found {files.Count}.");
        Assert.True(violations.Count == 0,
            "An inline text element (span, strong, em, small, OmniText that is not a paragraph) carries an "
            + "omni-u-mt-*/mb-*/my-* class that does nothing. Make it OmniTextElement.Title or Caption (block text "
            + "since OE 1.9.0), a paragraph "
            + "or an OmniHeading, or put it in an OmniStack with a Gap (STD-CARDSPACE):\n  "
            + string.Join("\n  ", violations));
    }

    [Theory]
    [InlineData("<OmniCard><OmniText Element=\"OmniTextElement.Strong\" Class=\"omni-u-mb-sm\">t</OmniText><OmniDataGrid/></OmniCard>")]
    [InlineData("<OmniCard><OmniText Class=\"omni-u-mt-md\">t</OmniText></OmniCard>")]
    [InlineData("<div><strong class=\"omni-u-mb-sm\">t</strong></div>")]
    [InlineData("<OmniCard><OmniText Element=\"OmniTextElement.Small\" Class=\"omni-u-text-muted omni-u-mb-xs\">t</OmniText></OmniCard>")]
    public void Scanner_FlagsInlineTextWithAVerticalMarginInAFlowParent(string markup) =>
        Assert.Single(Scan(markup));

    [Theory]
    [InlineData("<OmniCard><OmniText Element=\"OmniTextElement.Strong\" Class=\"omni-u-mb-sm omni-u-block\">t</OmniText></OmniCard>")]
    [InlineData("<OmniCard><OmniText Element=\"OmniTextElement.Paragraph\" Class=\"omni-u-mb-sm\">t</OmniText></OmniCard>")]
    [InlineData("<OmniCard><OmniText Element=\"OmniTextElement.Title\" Class=\"omni-u-mb-sm\">t</OmniText></OmniCard>")]
    [InlineData("<OmniCard><OmniText Element=\"OmniTextElement.Caption\" Class=\"omni-u-mt-sm\">t</OmniText></OmniCard>")]
    [InlineData("<OmniCard><OmniText Truncate=\"true\" Class=\"omni-u-mb-sm\">t</OmniText></OmniCard>")]
    [InlineData("<OmniCard><OmniHeading Class=\"omni-u-mb-sm\">t</OmniHeading></OmniCard>")]
    [InlineData("<OmniCard><OmniText Element=\"OmniTextElement.Strong\">t</OmniText></OmniCard>")]
    [InlineData("<OmniCard><OmniText Element=\"OmniTextElement.Strong\" Class=\"omni-u-ms-sm\">t</OmniText></OmniCard>")]
    [InlineData("<OmniStack><OmniText Element=\"OmniTextElement.Strong\" Class=\"omni-u-mb-sm\">t</OmniText></OmniStack>")]
    [InlineData("<div class=\"flex-row\"><OmniText Class=\"omni-u-mb-sm\">t</OmniText></div>")]
    [InlineData("<OmniCard><OmniText Class=\"omni-u-mb-sm blocky\">t</OmniText></OmniCard>")]
    public void Scanner_AcceptsBlockTextOrAParentThatAlreadyLaysItOut(string markup) =>
        Assert.Empty(Scan(markup, "blocky"));

    private static List<BlockSpacingScanner.Violation> Scan(string markup, params string[] displayClasses) =>
        BlockSpacingScanner.ScanInlineMargins(markup, new HashSet<string>(displayClasses));

    private static HashSet<string> LoadDisplayClasses(string front)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var sheets = RepositoryScan.Enumerate(Path.Combine(front, "wwwroot", "css"), "*.css")
            .Concat(RepositoryScan.EnumerateOptional(front, "*.razor.css"));
        foreach (var sheet in sheets)
        {
            foreach (Match rule in CssRule.Matches(File.ReadAllText(sheet)))
            {
                if (!DisplayDeclaration.IsMatch(rule.Groups["body"].Value))
                {
                    continue;
                }

                foreach (var selector in rule.Groups["sel"].Value.Split(','))
                {
                    var subject = selector.Trim().Split([' ', '>', '+', '~'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    foreach (Match name in ClassName.Matches(subject ?? string.Empty))
                    {
                        classes.Add(name.Groups["c"].Value);
                    }
                }
            }
        }

        return classes;
    }
}
