// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R-112 (rules 4 and 5 of R-30): text wraps only when there is no room, and a page is usable at 375 px.
/// These guards read <c>app.css</c>: they stop a new single-line rule cut with an ellipsis, and a new fixed width
/// wider than a phone, from entering silently. Whether a given text really wraps, or a row really fits, is measured
/// on screen at 375 px, not here.
/// </summary>
public sealed class TextWrappingAuditTests
{
    private sealed record Rule(string Selector, string Body, string? Media);

    /// <summary>The widest content a phone gives a block: 375 px minus the page gutters.</summary>
    private const double PhoneContentRem = 21.9;

    private static readonly string Css = Regex.Replace(
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")),
        @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>
    /// Every selector whose rule cuts its text with an ellipsis, and why that is right there. A text a user must read
    /// (a name, a message, a note, a path) wraps instead and has no entry. Each kept text carries its full value in a
    /// <c>title</c> (or, in a grid, OE's cell tooltip), so nothing is lost by the cut.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> EllipsisAllowList = new Dictionary<string, string>
    {
        [".omni-data-grid tr[data-omni-row-index] > td"] =
            "Grid cells keep one constant row height (R-6); the columns fit their content, so the cut only shows when the reader narrows a column, and OE's cell tooltip then carries the whole value.",
        [".analysis-finding-grid-title"] =
            "Title in a fixed-height grid row; the link carries the whole title as its tooltip.",
        [".analysis-finding-location"] =
            "File location in a fixed-height grid row, cut at its start to keep the file name; the cell carries the whole path as its title.",
        [".project-card-desc"] =
            "A multi-line clamp (not a single line) of a project's free-text description on its card; the card carries the whole description as its title and the project page shows it.",
        [".project-dense-description"] =
            "Dense project list: a fixed-height row cell; the span carries the whole description as its title. The list scrolls sideways inside its frame.",
        [".project-activity-line.project-activity-ellipsis"] =
            "Project card (R-27): the pipeline name and the step of a run in progress each hold one line so a tile with a run stays as tall as one without; the span carries the whole text as its title (OE title tooltip).",
        [".project-dense-activity > span"] =
            "Dense project list: the commit message or run step in a fixed-height row cell; the span carries the whole text as its title.",
        [".project-dense-activity a"] =
            "Dense project list: the run link in a fixed-height row cell; the link carries the whole text as its title.",
        [".run-gantt-cols > span"] =
            "Figures in the fixed 4rem columns that line the stage rows up; each carries its whole value as its title.",
        [".run-tl-stage-label"] =
            "A short stage label (CI, QA, DEPLOY) in a fixed 3.75rem column that aligns the trigger rows; it carries the whole label as its title.",
        [".vp-node-header-title"] =
            "Stage name on a fixed-size node card of the visual editor canvas; the node carries the whole name as its title.",
        [".vp-node-step-name"] =
            "Step name on a fixed-size node card of the visual editor canvas; the node carries the whole name as its title.",
        [".app-title"] =
            "The application name in the fixed-height header bar, beside the menu button.",
        [".dev-env-chip-text"] =
            "Environment, label and branch chips of the fixed-height header bar; each chip carries its whole value as its title.",
        [".log-message-cell"] =
            "System log message in a grid cell of constant height; the whole text is the cell's title and the row's view-message action.",
        [".git-graph-subject"] =
            "Commit subject on a graph row whose height is fixed by the lane drawing beside it; it carries the whole subject as its title.",
        [".library-cell > .clickable-cell"] =
            "Key of a vault secret in a grid cell of constant height; it carries the whole key as its title.",
        [".library-cell > .library-cell-text"] =
            "Key or value of a variable in a grid cell of constant height; it carries the whole text as its title.",
        [".resource-usage"] =
            "The readers of a resource in a grid cell of constant height; all names are the title and the count opens the list.",
        [".certbot-domains"] =
            "The single domain of a certificate in a grid cell of constant height (R-123); it is the cell's title, and from two domains the cell is a count badge that opens the list.",
        [".app-log-cell-text"] =
            "Application log message or attributes in a grid cell of constant height; it carries the whole text as its title.",
    };

    /// <summary>
    /// A fixed width wider than a phone that is deliberate, with how it gives way at 375 px: <c>Reset</c> when a rule of
    /// a phone media query drops it (min-width back to 0), <c>Scrolls:&lt;container&gt;</c> when the element sits in a
    /// container whose own rule scrolls sideways (<c>overflow-x: auto</c>), so the page itself never overflows.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Selector, string Property), (string How, string Reason)> WideAllowList =
        new Dictionary<(string, string), (string, string)>
        {
            [(".project-dense-header", "min-width")] = ("Scrolls:.project-dense-list", "The dense project list is a column grid: the user decided it scrolls sideways inside its frame without hiding a column (ResponsiveGridColumnAuditTests)."),
            [(".project-dense-row", "min-width")] = ("Scrolls:.project-dense-list", "Same: the rows scroll with the header, inside the same frame."),
            [(".pipeline-run-tree-row", "min-width")] = ("Reset", "The run tree row is a 52rem grid; on a phone it is a wrapping line."),
            [(".pipeline-run-tree-with-project .pipeline-run-tree-row", "min-width")] = ("Reset", "Same, with the project column."),
            [(".pipeline-run-tree-compact .pipeline-run-tree-row", "min-width")] = ("Reset", "Same, compact."),
            [(".pipeline-run-tree-compact.pipeline-run-tree-with-project .pipeline-run-tree-row", "min-width")] = ("Reset", "Same, compact with the project column."),
            [(".pipeline-run-tree-compact.pipeline-run-tree-with-duration .pipeline-run-tree-row", "min-width")] = ("Reset", "Same, compact with the duration column."),
            [(".pipeline-run-tree-compact.pipeline-run-tree-with-duration.pipeline-run-tree-with-project .pipeline-run-tree-row", "min-width")] = ("Reset", "Same, compact with both."),
            [(".project-repo-url-input", "min-width")] = ("Reset", "The repository URL field is 22rem wide on a desktop; on a phone it takes the width it has."),
            [(".onboarding-steps", "min-width")] = ("Reset", "The welcome steps table keeps 34rem beside the side menu; on a phone its cells wrap."),
        };

    private static readonly IReadOnlyList<Rule> Rules = Parse(Css, media: null);

    private static List<Rule> Parse(string text, string? media)
    {
        var rules = new List<Rule>();
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('{', index);
            if (open < 0) break;
            var prelude = text[index..open].Trim();
            var close = MatchingBrace(text, open);
            var inner = text[(open + 1)..close];
            if (prelude.StartsWith("@media", StringComparison.Ordinal))
                rules.AddRange(Parse(inner, prelude));
            else if (!prelude.StartsWith('@'))
                rules.Add(new Rule(Regex.Replace(prelude, @"\s+", " "), Regex.Replace(inner, @"\s+", " ").Trim(), media));
            index = close + 1;
        }

        return rules;
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}' && --depth == 0) return index;
        }

        throw new InvalidOperationException("Unbalanced braces in app.css.");
    }

    private static IEnumerable<string> Selectors(Rule rule) =>
        rule.Selector.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0);

    private static IEnumerable<string> EllipsisSelectors(IEnumerable<Rule> rules) =>
        rules.Where(rule => Regex.IsMatch(rule.Body, @"(?<![-\w])text-overflow\s*:\s*ellipsis"))
            .SelectMany(Selectors)
            .Distinct(StringComparer.Ordinal);

    /// <summary>A phone media query: the widest width it applies up to is at most 48rem.</summary>
    private static bool IsPhoneMedia(string? media)
    {
        var match = media is null ? Match.Empty : Regex.Match(media, @"max-width\s*:\s*(?<value>[\d.]+)(?<unit>rem|px)");
        if (!match.Success) return false;
        var rem = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)
            / (match.Groups["unit"].Value == "px" ? 16 : 1);
        return rem <= 48;
    }

    private static IEnumerable<(string Selector, string Property, double Rem)> WideDeclarations(IEnumerable<Rule> rules)
    {
        foreach (var rule in rules)
        {
            // A rule that only applies from a width up (min-width media) is not a phone rule.
            if (rule.Media is not null && Regex.IsMatch(rule.Media, @"min-width")) continue;
            // A block capped by its container never exceeds it.
            if (Regex.IsMatch(rule.Body, @"(?<![-\w])max-(width|inline-size)\s*:\s*100%|(?<![-\w])(min|max)\(")) continue;

            foreach (Match declaration in Regex.Matches(rule.Body,
                @"(?<![-\w])(?<property>min-width|min-inline-size|width|inline-size|flex-basis)\s*:\s*(?<value>\d+(?:\.\d+)?)(?<unit>rem|px)\b"))
            {
                var rem = double.Parse(declaration.Groups["value"].Value, CultureInfo.InvariantCulture)
                    / (declaration.Groups["unit"].Value == "px" ? 16 : 1);
                if (rem <= PhoneContentRem) continue;
                foreach (var selector in Selectors(rule))
                {
                    yield return (selector, NormalProperty(declaration.Groups["property"].Value), rem);
                }
            }
        }
    }

    private static string NormalProperty(string property) => property switch
    {
        "min-inline-size" => "min-width",
        "inline-size" => "width",
        _ => property,
    };

    [Fact]
    public void EveryRuleThatCutsTextWithAnEllipsis_IsListedWithItsReason()
    {
        var found = EllipsisSelectors(Rules).ToList();
        Assert.NotEmpty(found);

        var unlisted = found.Where(selector => !EllipsisAllowList.ContainsKey(selector)).ToList();
        Assert.True(unlisted.Count == 0,
            "R-112: text wraps when it has no room; a text the user must read is never cut with an ellipsis. "
            + "Let these wrap, or (a fixed-size cell only, with the whole value as a title) list them with a reason in "
            + "EllipsisAllowList:\n  " + string.Join("\n  ", unlisted));
    }

    [Fact]
    public void EveryListedEllipsis_StillExists()
    {
        var found = EllipsisSelectors(Rules).ToHashSet(StringComparer.Ordinal);
        var stale = EllipsisAllowList.Keys.Where(selector => !found.Contains(selector)).ToList();
        Assert.True(stale.Count == 0,
            "These selectors no longer cut text with an ellipsis; drop them from EllipsisAllowList:\n  "
            + string.Join("\n  ", stale));
        Assert.All(EllipsisAllowList.Values, reason => Assert.True(reason.Length > 40, "A reason says why the cut is right."));
    }

    [Fact]
    public void EveryFixedWidthWiderThanAPhone_GivesWayOnAPhone()
    {
        var wide = WideDeclarations(Rules).ToList();
        Assert.NotEmpty(wide);

        var unlisted = wide.Where(entry => !WideAllowList.ContainsKey((entry.Selector, entry.Property)))
            .Select(entry => $"{entry.Selector} {{ {entry.Property}: {entry.Rem}rem }}")
            .ToList();
        Assert.True(unlisted.Count == 0,
            "R-112: a width fixed beyond 21.9rem overflows a 375 px phone. Cap it (max-width: 100%, min(..)) or list it in "
            + "WideAllowList with how it gives way on a phone:\n  " + string.Join("\n  ", unlisted));
    }

    [Fact]
    public void EveryListedWideWidth_IsReallyDroppedByAPhoneRule_AndStillExists()
    {
        var wide = WideDeclarations(Rules).Select(entry => (entry.Selector, entry.Property)).ToHashSet();
        foreach (var ((selector, property), (how, _)) in WideAllowList)
        {
            Assert.True(wide.Contains((selector, property)),
                $"'{selector}' {property} is no longer a fixed width beyond a phone; drop it from WideAllowList.");

            if (how.StartsWith("Scrolls:", StringComparison.Ordinal))
            {
                var container = how["Scrolls:".Length..];
                Assert.True(
                    Rules.Any(rule => Selectors(rule).Contains(container, StringComparer.Ordinal)
                        && Regex.IsMatch(rule.Body, @"(?<![-\w])overflow-x\s*:\s*(auto|scroll)\b")),
                    $"'{selector}' keeps its wide {property}, but '{container}' no longer scrolls sideways around it.");
                continue;
            }

            var phoneRules = Rules.Where(rule => IsPhoneMedia(rule.Media) && Selectors(rule).Contains(selector, StringComparer.Ordinal)).ToList();
            Assert.True(
                phoneRules.Any(rule => Regex.IsMatch(rule.Body, @"(?<![-\w])(min-width|min-inline-size)\s*:\s*(0|auto)\b")),
                $"'{selector}' keeps its wide {property} on a phone: no phone media rule resets it.");
        }
    }

    [Fact]
    public void TheScanners_SeeWhatTheyAreMeantToCatch()
    {
        // Non-vacuity: the guard logic on a sheet written for the purpose.
        var sample = Parse(
            ".a { text-overflow: ellipsis; } .b, .c { overflow: hidden; text-overflow: ellipsis } "
            + ".d { min-width: 30rem } .e { width: 30rem; max-width: 100% } .f { min-width: 600px } "
            + "@media (max-width: 39.99rem) { .d { min-width: 0 } } @media (min-width: 40rem) { .g { width: 50rem } }", null);

        Assert.Equal(new[] { ".a", ".b", ".c" }, EllipsisSelectors(sample).Order());
        Assert.Equal(new[] { (".d", "min-width"), (".f", "min-width") },
            WideDeclarations(sample).Select(entry => (entry.Selector, entry.Property)).Order());
        Assert.True(IsPhoneMedia("@media (max-width: 39.99rem)"));
        Assert.False(IsPhoneMedia("@media (max-width: 79.99rem)"));
        Assert.False(IsPhoneMedia("@media (min-width: 40rem)"));
    }
}
