// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Guards the UrlSyncedTabs convention per individual OmniTabs container. Exceptions
/// identify one exact container and expected tab count, never an entire file.
/// </summary>
public class UrlSyncedTabsAuditTests
{
    private static readonly AllowedContainer[] Allowlist =
    [
        new(
            Path.Combine("Components", "Settings", "Settings.razor"),
            0,
            5,
            "route-based tabs (/settings/{tab})"),
        new(
            Path.Combine("Components", "Users", "AdminIdentityNav.razor"),
            0,
            3,
            "route-based tabs (/admin/users, /admin/organizations, /admin/roles), R-7: each tab is its own page, no panel content"),
        new(
            Path.Combine("Components", "Servers", "ServerDetailSections", "DockerProjectDialog.razor"),
            0,
            5,
            "transient project-details dialog with no route"),
        new(
            Path.Combine("Components", "AiTasks", "AiResultDialog.razor"),
            0,
            2,
            "transient AI-result dialog with no route"),
        new(
            Path.Combine("Components", "Projects", "AppTelemetryPanel.razor"),
            0,
            7,
            "telemetry sub-tabs scoped to the application picked in the panel, not deep-linked (PLAN-013 lot 1 added the Database tab)"),
    ];

    private static readonly Regex TabsTokenRegex = new(
        @"</OmniTabs>|<OmniTabs(?=[\s>])|<OmniTabsItem(?=[\s>])",
        RegexOptions.Compiled);

    [Fact]
    public void MultiTab_Containers_Use_UrlSyncedTabs_Or_An_Exact_Exception()
    {
        var frontDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        Assert.True(Directory.Exists(frontDir), $"Front dir not found: {frontDir}");

        var violations = new List<string>();
        var matchedExceptions = new HashSet<AllowedContainer>();
        var containersScanned = 0;

        foreach (var file in RepositoryScan.Enumerate(frontDir, "*.razor"))
        {
            var raw = StripRazorComments(File.ReadAllText(file));
            var rel = Path.GetRelativePath(frontDir, file);

            foreach (var container in EnumerateContainers(raw))
            {
                containersScanned++;
                var exception = Allowlist.SingleOrDefault(item =>
                    item.Path.Equals(rel, StringComparison.OrdinalIgnoreCase)
                    && item.Ordinal == container.Ordinal);

                if (exception is not null)
                {
                    matchedExceptions.Add(exception);
                    if (container.TabCount != exception.ExpectedTabCount)
                    {
                        violations.Add(
                            $"{rel}:{container.Line} exception #{container.Ordinal} expected "
                            + $"{exception.ExpectedTabCount} tabs but found {container.TabCount}");
                    }
                    continue;
                }

                if (container.TabCount >= 2)
                {
                    violations.Add(
                        $"{rel}:{container.Line} bare <OmniTabs> #{container.Ordinal} has "
                        + $"{container.TabCount} tabs - use <UrlSyncedTabs> or add an exact exception");
                }
            }
        }

        foreach (var stale in Allowlist.Except(matchedExceptions))
            violations.Add($"{stale.Path}: stale exception for container #{stale.Ordinal} ({stale.Reason})");

        Assert.True(containersScanned >= Allowlist.Length,
            $"Scanner found only {containersScanned} bare OmniTabs containers.");
        Assert.Empty(violations);
    }

    /// <summary>
    /// OE selects a tab by its key, and UrlSyncedTabs hands it the slug of the tab in the address. A tab
    /// placed in a UrlSyncedTabs without <c>Key</c> falls back to its (localized) title, never matches a
    /// slug, and can then never be shown selected. Where the slugs are written inline, the keys must be
    /// exactly those slugs, in order.
    /// </summary>
    [Fact]
    public void Every_Tab_In_UrlSyncedTabs_Is_Keyed_By_Its_Slug()
    {
        var frontDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var violations = new List<string>();
        var scanned = 0;

        foreach (var file in RepositoryScan.Enumerate(frontDir, "*.razor"))
        {
            var raw = StripRazorComments(File.ReadAllText(file));
            var rel = Path.GetRelativePath(frontDir, file);
            foreach (Match wrapper in WrapperRegex.Matches(raw))
            {
                var keys = new List<string>();
                foreach (Match item in TopLevelItems(wrapper.Groups["body"].Value))
                {
                    scanned++;
                    var key = KeyRegex.Match(item.Value);
                    if (key.Success) keys.Add(key.Groups["key"].Value);
                    else violations.Add($"{rel}:{LineNumberAt(raw, wrapper.Index)} OmniTabsItem without a literal Key");
                }

                var inlineSlugs = InlineSlugsRegex.Match(wrapper.Groups["open"].Value);
                if (!inlineSlugs.Success) continue;
                var slugs = Regex.Matches(inlineSlugs.Groups["list"].Value, "\"(?<slug>[^\"]+)\"")
                    .Select(slug => slug.Groups["slug"].Value)
                    .ToList();
                if (!slugs.SequenceEqual(keys))
                    violations.Add($"{rel}:{LineNumberAt(raw, wrapper.Index)} keys [{string.Join(", ", keys)}] "
                        + $"differ from slugs [{string.Join(", ", slugs)}]");
            }
        }

        Assert.True(scanned > 0, "No tab found in a UrlSyncedTabs. A guard that scans nothing passes vacuously.");
        Assert.Empty(violations);
    }

    private static readonly Regex WrapperRegex = new(
        @"(?<open><UrlSyncedTabs\b[^>]*>)(?<body>.*?)</UrlSyncedTabs>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex KeyRegex = new(@"^<OmniTabsItem\s+Key=""(?<key>[^""@]+)""", RegexOptions.Compiled);

    private static readonly Regex InlineSlugsRegex = new(
        @"Slugs=""@\(new\[\]\s*\{(?<list>[^}]*)\}\)""",
        RegexOptions.Compiled);

    /// <summary>The opening tags of the OmniTabsItems of this tab set, not those of a tab set nested in a tab.</summary>
    private static IEnumerable<Match> TopLevelItems(string body)
    {
        var depth = 0;
        foreach (Match token in Regex.Matches(body, @"</OmniTabsItem>|<OmniTabsItem\b[^>]*?/?>"))
        {
            if (token.Value.StartsWith("</", StringComparison.Ordinal)) { depth--; continue; }
            if (depth == 0) yield return token;
            if (!token.Value.EndsWith("/>", StringComparison.Ordinal)) depth++;
        }
    }

    [Fact]
    public void Scanner_Counts_Each_Container_Independently()
    {
        const string markup = """
            <OmniTabs><Tabs><OmniTabsItem /></Tabs></OmniTabs>
            <OmniTabs>
                <Tabs>
                    <OmniTabsItem />
                    <OmniTabsItem />
                </Tabs>
            </OmniTabs>
            """;

        var containers = EnumerateContainers(markup);

        Assert.Collection(
            containers,
            first =>
            {
                Assert.Equal(0, first.Ordinal);
                Assert.Equal(1, first.TabCount);
            },
            second =>
            {
                Assert.Equal(1, second.Ordinal);
                Assert.Equal(2, second.TabCount);
            });
    }

    [Fact]
    public void SharedTabs_ReserveStableGeometry()
    {
        var frontDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var wrapper = File.ReadAllText(Path.Combine(frontDir, "Components", "Shared", "UrlSyncedTabs.razor"));
        var css = File.ReadAllText(Path.Combine(frontDir, "wwwroot", "css", "app.css"));

        Assert.Contains("Class=\"@CssClass\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("scrollbar-gutter: stable", css, StringComparison.Ordinal);
        Assert.Contains(".url-synced-tabs > .omni-tabs__strip .omni-tabs__viewport", css, StringComparison.Ordinal);
        Assert.Contains("flex: 1 1 0", css, StringComparison.Ordinal);
        Assert.Contains(".omni-tabs > .omni-tabs__strip .omni-tabs__viewport .omni-tabs__tab", css, StringComparison.Ordinal);
        Assert.Contains("width: 100%", css, StringComparison.Ordinal);
        Assert.Contains("scrollbar-width: none", css, StringComparison.Ordinal);
    }

    private static List<TabsContainer> EnumerateContainers(string source)
    {
        var result = new List<TabsContainer>();
        var stack = new Stack<OpenContainer>();
        var ordinal = 0;

        foreach (var token in TabsTokenRegex.Matches(source).Cast<Match>())
        {
            if (token.Value.StartsWith("<OmniTabsItem", StringComparison.Ordinal))
            {
                if (stack.TryPeek(out var current)) current.TabCount++;
                continue;
            }

            if (token.Value.StartsWith("</", StringComparison.Ordinal))
            {
                if (stack.TryPop(out var closed))
                    result.Add(new TabsContainer(closed.Ordinal, closed.TabCount, closed.Line));
                continue;
            }

            var tagEnd = FindTagEnd(source, token.Index);
            if (tagEnd < 0) continue;
            if (source.AsSpan(token.Index, tagEnd - token.Index + 1).TrimEnd().EndsWith("/>"))
            {
                result.Add(new TabsContainer(ordinal++, 0, LineNumberAt(source, token.Index)));
                continue;
            }

            stack.Push(new OpenContainer(ordinal++, LineNumberAt(source, token.Index)));
        }

        result.Sort((left, right) => left.Ordinal.CompareTo(right.Ordinal));
        return result;
    }

    private static int FindTagEnd(string source, int start)
    {
        var inQuote = false;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '"') inQuote = !inQuote;
            else if (source[i] == '>' && !inQuote) return i;
        }
        return -1;
    }

    private static int LineNumberAt(string source, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++) if (source[i] == '\n') line++;
        return line;
    }

    private static string StripRazorComments(string source) =>
        Regex.Replace(source, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

    private sealed record AllowedContainer(string Path, int Ordinal, int ExpectedTabCount, string Reason);
    private sealed record TabsContainer(int Ordinal, int TabCount, int Line);

    private sealed class OpenContainer(int ordinal, int line)
    {
        public int Ordinal { get; } = ordinal;
        public int Line { get; } = line;
        public int TabCount { get; set; }
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
