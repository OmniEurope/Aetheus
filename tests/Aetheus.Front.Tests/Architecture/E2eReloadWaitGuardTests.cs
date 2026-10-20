// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// A <c>ReloadAsync</c> in an E2E test cold-boots the WASM frontend, which on a loaded QA agent takes
/// longer than Playwright's 5s default. Nightly 2697 was rejected after 2h50 because the first wait
/// after a reload used that default. This guard requires the first wait that follows every reload
/// to carry the boot budget (<c>PlaywrightConfig.AppReadyTimeoutMs</c>) or to be the
/// <c>WaitForBlazorAsync</c> helper, which uses it.
/// </summary>
public class E2eReloadWaitGuardTests
{
    private static readonly Regex ReloadRegex = new(@"\.ReloadAsync\(", RegexOptions.Compiled);

    // The first wait after the reload: a web-first assertion, a Playwright WaitFor*, or the boot helper.
    private static readonly Regex WaitRegex = new(@"\bExpect\(|\.WaitFor\w*Async\(|\bWaitForBlazorAsync\(", RegexOptions.Compiled);

    private static readonly Regex LineCommentRegex = new(@"//.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void Every_Reload_Is_Followed_By_A_Wait_With_The_Boot_Budget()
    {
        var e2eDir = Path.Combine(RepositoryScan.Root, "tests", "Aetheus.E2E");
        var files = RepositoryScan.Enumerate(e2eDir, "*.cs");
        var reloads = 0;
        var violations = new List<string>();

        foreach (var file in files)
        {
            var lines = FindViolations(File.ReadAllText(file), out var count);
            reloads += count;
            violations.AddRange(lines.Select(line =>
                $"{Path.GetRelativePath(RepositoryScan.Root, file).Replace('\\', '/')}:{line}"));
        }

        Assert.True(reloads > 0, "The scan found no ReloadAsync in the E2E sources: the path resolution is broken.");
        Assert.True(violations.Count == 0,
            "A reload cold-boots the frontend: the first wait after it must use PlaywrightConfig.AppReadyTimeoutMs "
            + "or WaitForBlazorAsync(), never the 5s default or a fixed number (nightly 2697):\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Flags_A_Default_Or_Fixed_Timeout_And_Accepts_The_Boot_Budget()
    {
        const string defaultTimeout = """
            await Page.ReloadAsync();
            await Expect(bars).ToHaveCountAsync(4);
            """;
        const string fixedTimeout = """
            await Page.ReloadAsync();
            // PlaywrightConfig.AppReadyTimeoutMs in a comment does not count
            await Page.WaitForSelectorAsync("x", new() { Timeout = 10000 });
            """;
        const string noWait = "await Page.ReloadAsync();\nvar x = 1;";
        const string budget = """
            await Page.ReloadAsync();
            await Expect(bars).ToHaveCountAsync(4, new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
            """;
        const string helper = "await Page.ReloadAsync();\nawait WaitForBlazorAsync();\nawait Expect(x).ToBeVisibleAsync();";

        Assert.Single(FindViolations(defaultTimeout, out _));
        Assert.Single(FindViolations(fixedTimeout, out _));
        Assert.Single(FindViolations(noWait, out _));
        Assert.Empty(FindViolations(budget, out _));
        Assert.Empty(FindViolations(helper, out _));
    }

    /// <summary>Returns the 1-based line of every reload whose first following wait lacks the boot budget.</summary>
    private static List<int> FindViolations(string source, out int reloadCount)
    {
        // Comments are blanked, not removed, so match offsets still map to the original lines.
        var code = LineCommentRegex.Replace(source, m => new string(' ', m.Length));
        var violations = new List<int>();
        var reloads = ReloadRegex.Matches(code);
        reloadCount = reloads.Count;

        foreach (Match reload in reloads)
        {
            var line = code[..reload.Index].Count(c => c == '\n') + 1;
            var afterReload = code.IndexOf(';', reload.Index);
            var wait = afterReload < 0 ? Match.Empty : WaitRegex.Match(code, afterReload);
            if (!wait.Success)
            {
                violations.Add(line);
                continue;
            }

            var waitEnd = code.IndexOf(';', wait.Index);
            var window = code[afterReload..(waitEnd < 0 ? code.Length : waitEnd)];
            if (!window.Contains("AppReadyTimeoutMs", StringComparison.Ordinal)
                && !wait.Value.StartsWith("WaitForBlazorAsync", StringComparison.Ordinal))
                violations.Add(line);
        }

        return violations;
    }
}
