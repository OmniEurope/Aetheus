// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// R-116 (rule 10 of the all-pages audit): a long ACTION (a refresh, a fetch, a verification, an apply...) shows OE's
/// busy veil over its zone (<c>omni-busy</c> + <c>aria-busy</c>), the content of the zone stays mounted underneath. It
/// never adds a loader beside the content and never replaces the content by one. The first load of a page or panel,
/// while nothing exists yet, keeps <c>AetheusLoader</c> (decision D11, see <see cref="LoadingIndicatorAuditTests"/>).
/// The guard reads every <c>.razor</c> file: an <c>AetheusLoader</c>, or a progress bar without a value, rendered under a
/// condition that names an action flag (<c>_running</c>, <c>_saving</c>, <c>_fetching</c>, <c>_verifying</c>,
/// <c>_pendingTaskId</c>...) is refused, unless the same condition also tests that nothing exists yet
/// (<c>x is null</c>, <c>Count == 0</c>, <c>string.IsNullOrEmpty(x)</c>: a first load), or the place is listed in
/// <see cref="Allowed"/> with its reason. A progress bar that carries a <c>Value</c> shows real progress and is not a loader.
/// It does not judge a plain <c>_loading</c> flag (a first load in most pages) and cannot see a loader chosen inside a
/// code-behind or a ternary: the veil of those zones is proved by the behaviour tests.
/// </summary>
public sealed class ActionLoaderVeilGuardTests
{
    /// <summary>Flag names that mean "an action is running", as a whole word of a C# identifier.</summary>
    private static readonly Regex ActionFlag = new(
        @"\b_?\w*(Running|Saving|Busy|Fetching|Refreshing|Verifying|Applying|Reinstalling|Installing|Deleting|Restoring|Submitting|"
        + @"Publishing|Purging|Transferring|Testing|Checking|Contacting|Sending|Starting|Stopping|Pending|LoadingPreview|LogsLoading|"
        + @"GroupsLoading|GraphLoading|YamlLoading|Waiting|Executing|Generating|Scanning|Syncing|Updating|Reloading|Retrying|InFlight)\w*\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A component's own state field compared with a running member of its state enum (<c>_state == ContactState.Running</c>).
    /// A status read off a data row (<c>task.Status == X.Running</c>) is the data, not an action of this component, and is not matched.</summary>
    private static readonly Regex StateFieldRunning = new(
        @"\b_\w+\s*(?:==|is)\s*[\w.]*\.(?:Running|Pending|Busy|Saving|Verifying|Applying|Installing|Deleting|Restoring|Testing|Checking|Starting|Stopping|Waiting|Executing)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The condition also says that nothing exists yet: a first load, where the loader is the rule.</summary>
    private static readonly Regex NothingYet = new(
        @"\bis\s+null\b|==\s*null\b|\.Count\s*==\s*0|\.Length\s*==\s*0|IsNullOrEmpty|IsNullOrWhiteSpace|!\s*\w+\.Any\(\)|!\s*\w+\.ContainsKey\(|\bis\s*\{\s*Count:\s*0",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Loader = new(
        @"<AetheusLoader\b|<OmniProgressBar\b(?:(?:""[^""]*"")|[^>])*?/>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A loader under an action flag that is deliberately kept, with the reason. A stale entry fails.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // Key "relative/path.razor|flagName". Add an entry only with the user's decision.
        ["Components/Servers/ServerDetailSections/ServiceActionsInFlightBanner.razor|InFlightActions"] =
            "Recette R2-030: the user asked for a labelled linear bar on a service row while its operation runs; "
            + "LoadingIndicatorAuditTests allows this one shape, nothing spins.",
    };

    [Fact]
    public void NoLoader_IsRenderedForAnAction_TheZoneIsVeiledInstead()
    {
        var frontDir = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var violations = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var loadersSeen = 0;

        foreach (var file in RepositoryScan.Enumerate(frontDir, "*.razor"))
        {
            var relative = Path.GetRelativePath(frontDir, file).Replace('\\', '/');
            var source = File.ReadAllText(file);
            loadersSeen += Loader.Matches(source).Count;
            foreach (var hit in Scan(source))
            {
                var key = $"{relative}|{hit.Flag}";
                if (Allowed.ContainsKey(key))
                {
                    used.Add(key);
                    continue;
                }
                violations.Add($"{relative}:{hit.Line} renders a loader under '{hit.Condition}' (action flag {hit.Flag})");
            }
        }

        // The scan must really see the loaders of the product, or it protects nothing.
        Assert.True(loadersSeen > 60, $"The scan found only {loadersSeen} loaders: it is broken.");
        var stale = Allowed.Keys.Where(key => !used.Contains(key)).ToList();
        Assert.True(stale.Count == 0, "These allowed loaders no longer exist, remove them:\n  " + string.Join("\n  ", stale));
        Assert.True(violations.Count == 0,
            "R-116: a long action veils its zone (class omni-busy and aria-busy on the zone, content kept mounted underneath); "
            + "a loader is only for the first load, where nothing exists yet:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void TheScan_FlagsALoaderUnderAnActionFlag()
    {
        const string source = "@if (_saving)\n{\n    <AetheusLoader />\n}\nelse\n{\n    <p>done</p>\n}";
        var hit = Assert.Single(Scan(source));
        Assert.Equal("_saving", hit.Flag);
        Assert.Equal(3, hit.Line);
    }

    [Fact]
    public void TheScan_FlagsAValuelessProgressBar_AndAnOuterConditionToo()
    {
        const string source = "@if (_fetching)\n{\n    <OmniStack>\n        @if (x)\n        {\n            <OmniProgressBar Shape=\"OmniProgressShape.Linear\" />\n        }\n    </OmniStack>\n}";
        var hit = Assert.Single(Scan(source));
        Assert.Equal("_fetching", hit.Flag);
    }

    [Fact]
    public void TheScan_FlagsAPendingTaskLoaderInAnElseIfChain()
    {
        const string source = "@if (a)\n{\n    <p>a</p>\n}\nelse if (_pendingTaskId is not null)\n{\n    <AetheusLoader Size=\"OmniControlSize.Small\" />\n}";
        var hit = Assert.Single(Scan(source));
        Assert.Equal("_pendingTaskId", hit.Flag);
    }

    [Fact]
    public void TheScan_FlagsALoaderUnderAComponentStateField_ButNotUnderAStatusOfADataRow()
    {
        var hit = Assert.Single(Scan("@if (_state == ContactState.Running)\n{\n    <AetheusLoader />\n}"));
        Assert.Equal("_state == ContactState.Running", hit.Flag);
        Assert.Empty(Scan("@if (task.Status == TaskExecutionStatus.Running)\n{\n    <AetheusLoader />\n}"));
    }

    [Fact]
    public void TheScan_AcceptsAFirstLoad_AProgressBarWithAValue_AndAPlainLoadingFlag()
    {
        Assert.Empty(Scan("@if (_graphLoading && _graphCommits.Count == 0)\n{\n    <AetheusLoader />\n}"));
        Assert.Empty(Scan("@if (_running && _status is null)\n{\n    <AetheusLoader />\n}"));
        Assert.Empty(Scan("@if (_running)\n{\n    <OmniProgressBar Value=\"@Percent\" Shape=\"OmniProgressShape.Circular\" />\n}"));
        Assert.Empty(Scan("@if (_loading)\n{\n    <AetheusLoader />\n}"));
        Assert.Empty(Scan("@if (!TabsReady)\n{\n    <AetheusLoader Size=\"OmniControlSize.Large\" />\n}"));
    }

    private sealed record Hit(int Line, string Condition, string Flag);

    /// <summary>The first action flag named by <paramref name="condition"/>, or null. A member reached through a dot
    /// (<c>TaskExecutionStatus.Running</c>, <c>task.Pending</c>) is a data value, not a flag of the component.</summary>
    private static string? FirstActionFlag(string condition)
    {
        foreach (Match match in ActionFlag.Matches(condition))
        {
            if (match.Index > 0 && condition[match.Index - 1] == '.') continue;
            return match.Value;
        }
        var state = StateFieldRunning.Match(condition);
        return state.Success ? state.Value : null;
    }

    /// <summary>Every loader of <paramref name="source"/> that sits under an action-flag condition without a "nothing yet" test.</summary>
    private static List<Hit> Scan(string source)
    {
        var hits = new List<Hit>();
        foreach (Match loader in Loader.Matches(source))
        {
            if (loader.Value.StartsWith("<OmniProgressBar", StringComparison.Ordinal)
                && loader.Value.Contains("Value=", StringComparison.Ordinal)) continue;
            var line = source.AsSpan(0, loader.Index).Count('\n') + 1;
            foreach (var condition in EnclosingConditions(source, loader.Index))
            {
                var flag = FirstActionFlag(condition);
                if (flag is null || NothingYet.IsMatch(condition)) continue;
                hits.Add(new Hit(line, condition.Trim(), flag));
                break;
            }
        }
        return hits;
    }

    /// <summary>The conditions of the <c>if</c> / <c>else if</c> blocks that contain <paramref name="index"/>, innermost first.
    /// A plain <c>else</c> carries no condition of its own and is skipped.</summary>
    private static IEnumerable<string> EnclosingConditions(string source, int index)
    {
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            if (source[i] == '}') depth++;
            else if (source[i] == '{')
            {
                if (depth > 0) { depth--; continue; }
                var condition = ConditionBefore(source, i);
                if (condition is not null) yield return condition;
            }
        }
    }

    /// <summary>The text inside the parentheses of the <c>if (...)</c> that opens the block at <paramref name="brace"/>, or null.</summary>
    private static string? ConditionBefore(string source, int brace)
    {
        var end = brace - 1;
        while (end >= 0 && char.IsWhiteSpace(source[end])) end--;
        if (end < 0 || source[end] != ')') return null;
        var depth = 0;
        for (var i = end; i >= 0; i--)
        {
            if (source[i] == ')') depth++;
            else if (source[i] == '(' && --depth == 0)
            {
                var keyword = source.AsSpan(0, i).TrimEnd();
                if (!keyword.EndsWith("if", StringComparison.Ordinal)) return null;
                return source.Substring(i + 1, end - i - 1);
            }
        }
        return null;
    }
}
