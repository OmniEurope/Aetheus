// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Badges retain complete single-line labels at every width; their containing groups wrap
/// instead.</summary>
public sealed class BadgeWrappingAuditTests
{
    private static readonly string Css = Regex.Replace(
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")),
        @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>The rules at the top level of the sheet, outside any <c>@media</c> or <c>@supports</c>.</summary>
    private static IEnumerable<(string Selector, string Body)> TopLevelRules()
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < Css.Length; i++)
        {
            if (Css[i] == '{')
            {
                if (depth == 0)
                {
                    // A statement at-rule (@import, @charset) ends with ';' and has no block of its own.
                    var prelude = Css[start..i];
                    var selector = prelude[(prelude.LastIndexOf(';') + 1)..].Trim();
                    var close = Css.IndexOf('}', i);
                    var nested = Css.IndexOf('{', i + 1);
                    if (!selector.StartsWith('@') && close >= 0 && (nested < 0 || close < nested))
                    {
                        yield return (selector, Css[(i + 1)..close]);
                        i = close;
                        start = close + 1;
                        continue;
                    }
                }
                depth++;
            }
            else if (Css[i] == '}')
            {
                depth--;
                if (depth == 0) start = i + 1;
            }
        }
    }

    private static IEnumerable<(string Selector, string Body)> AllRules() =>
        Regex.Matches(Css, @"(?<selector>[^{}@]+)\{(?<body>[^{}]*)\}")
            .Select(rule => (rule.Groups["selector"].Value.Trim(), rule.Groups["body"].Value));

    [Fact]
    public void ABadgeIsNeverSqueezed_AtEveryWidth()
    {
        Assert.Contains(TopLevelRules(), rule =>
            rule.Selector == ".omni-badge" && Regex.IsMatch(rule.Body, @"(?<![-\w])flex-shrink\s*:\s*0\b"));
    }

    [Fact]
    public void TheDashboardTileBadgeGroup_Wraps_AtEveryWidth()
    {
        Assert.Contains(TopLevelRules(), rule =>
            rule.Selector.Contains(".dashboard-tile .omni-stack:has(> .omni-badge)", StringComparison.Ordinal)
            && Regex.IsMatch(rule.Body, @"(?<![-\w])flex-wrap\s*:\s*wrap\b"));
    }

    [Fact]
    public void NoRule_LetsABadgeWrapBreakOrTruncate()
    {
        var offenders = AllRules()
            .Where(rule => rule.Selector.Split(',').Any(selector =>
                Regex.IsMatch(selector.Trim(), @"\.omni-badge(?:[.:\[][^\s>+~]*)?$")))
            .Where(rule => Regex.IsMatch(rule.Body,
                @"(?<![-\w])(white-space\s*:\s*(normal|pre-wrap|pre-line|break-spaces)|text-overflow\s*:\s*ellipsis|overflow-wrap\s*:\s*(anywhere|break-word)|word-break\s*:\s*(break-all|break-word)|flex-shrink\s*:\s*[1-9])"))
            .Select(rule => rule.Selector)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A badge keeps its text on one line (recette R-342); these rules let it wrap, break or truncate:\n  "
            + string.Join("\n  ", offenders));
    }
}
