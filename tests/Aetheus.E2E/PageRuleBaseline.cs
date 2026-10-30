// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.E2E;

/// <summary>
/// What a sweep found compared with the checked-in baseline. <see cref="New"/> and <see cref="Stale"/> fail the run.
/// <see cref="DataNew"/> and <see cref="DataGone"/> are the same differences for the rules whose outcome depends on the
/// rows on screen: they are reported and never fail the run.
/// </summary>
internal sealed record PageRuleRatchet(
    IReadOnlyList<string> New,
    IReadOnlyList<string> Stale,
    IReadOnlyList<string> DataNew,
    IReadOnlyList<string> DataGone)
{
    public bool IsClean => New.Count == 0 && Stale.Count == 0;
}

/// <summary>
/// The checked-in list of the violations the application has today, one line per violation:
/// <c>Page | Width | Rule | Element</c>. The sweep fails on a violation that is not listed (a regression) and on a listed
/// line that no longer occurs (a fix that must be recorded by deleting the line), so the list can only shrink.
/// The one exception is a rule whose outcome depends on the rows on screen (see <see cref="DataDependentRules"/>).
/// </summary>
internal static class PageRuleBaseline
{
    public const string ResourceName = "page-rules-baseline.txt";
    public const string SourcePath = "tests/Aetheus.E2E/PageRules/page-rules-baseline.txt";

    /// <summary>
    /// Whether a cell is cut depends on the longest value the grid holds, and the other E2E fixtures add audit entries,
    /// vaults and other rows to the shared database before this one runs. A run on the full suite showed three truncated
    /// cells (`/audit`, `/admin/audit`, `/projects/{Id}/vaults`) that a run on the initial data does not. Such a rule cannot
    /// be a two-sided gate: it would fail on the order of the fixtures, not on the application. Its lines stay in the
    /// baseline as the list the report compares against. 3.page-scroll-with-grid is the same (R-129): whether a page
    /// scrolls around its grid depends on how many rows the earlier fixtures gave it (the cron and docker grids of the
    /// test server, at 1280, scrolled in one full run and not in the next).
    /// </summary>
    public static readonly IReadOnlySet<string> DataDependentRules = new HashSet<string>(StringComparer.Ordinal)
    {
        "3.grid-cell-truncated",
        "3.page-scroll-with-grid"
    };

    private const string KeyFieldSeparator = " | ";
    private const int KeyRuleFieldIndex = 2;

    private const string Header = """
        # Page rules baseline (recette R-30): the violations of the presentation rules that exist today.
        # One line per violation: Page | Width | Rule | Element. Width 0 stands for a page that cannot be put on screen.
        # PageRulesTests fails on a violation that is not listed and on a listed line that no longer occurs.
        # Fix a page, then delete its lines. A line is never added by hand to turn a run green: the run writes
        # page-rules-baseline.proposed.txt next to its report, and a new line is a decision that is reviewed.
        # Exception: 3.grid-cell-truncated and 3.page-scroll-with-grid depend on the rows in the database, so their differences are reported, not failed.
        """;

    public static bool IsDataDependent(string key)
    {
        var fields = key.Split(KeyFieldSeparator, KeyRuleFieldIndex + 2);
        return fields.Length > KeyRuleFieldIndex && DataDependentRules.Contains(fields[KeyRuleFieldIndex]);
    }

    public static IReadOnlySet<string> Load()
    {
        using var stream = typeof(PageRuleBaseline).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The resource '{ResourceName}' is missing from Aetheus.E2E: the project file must embed {SourcePath}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#')) keys.Add(trimmed);
        }
        return keys;
    }

    public static string Format(IEnumerable<string> keys)
    {
        var builder = new StringBuilder(Header).AppendLine();
        foreach (var key in keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            builder.AppendLine(key);
        return builder.ToString();
    }

    public static PageRuleRatchet Compare(IReadOnlySet<string> baseline, IEnumerable<string> actual)
    {
        var current = actual.ToHashSet(StringComparer.Ordinal);
        var added = current.Where(key => !baseline.Contains(key)).Order(StringComparer.Ordinal).ToList();
        var removed = baseline.Where(key => !current.Contains(key)).Order(StringComparer.Ordinal).ToList();
        return new PageRuleRatchet(
            added.Where(key => !IsDataDependent(key)).ToList(),
            removed.Where(key => !IsDataDependent(key)).ToList(),
            added.Where(IsDataDependent).ToList(),
            removed.Where(IsDataDependent).ToList());
    }
}
