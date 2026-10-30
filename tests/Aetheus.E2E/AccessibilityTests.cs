// SPDX-License-Identifier: EUPL-1.2
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;

namespace Aetheus.E2E;

/// <summary>Scan authenticated pages with axe-core and fail on serious or critical WCAG violations
/// while exporting every impact as SARIF. Execution requires the running test application and
/// browsers.</summary>
[Category("E2E")]
[Category("Accessibility")]
[RollbackRelevant]
public class AccessibilityTests : E2ETestBase
{
    // The impacts we refuse to ship. Axe reports impact as one of
    // minor | moderate | serious | critical (lower-case strings).
    private static readonly string[] BlockingImpacts = ["serious", "critical"];

    // axe ruleset: WCAG 2.0/2.1 levels A and AA - the level the app commits to.
    private static readonly AxeRunOptions ScanOptions = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]
        }
    };

    [SetUp]
    public async Task SetUp() => await LoginAsync();

    [Test]
    public async Task Accessibility_KeyPages_HaveNoSeriousOrCriticalViolations()
    {
        var failures = new List<string>();
        var violations = new List<AccessibilityViolation>();
        foreach (var path in MainPages)
        {
            await NavigateToAsync(path);
            await WaitForNoSpinnerAsync();

            var result = await Page.RunAxe(ScanOptions);
            violations.AddRange(result.Violations.Select(v => new AccessibilityViolation(
                path, v.Id, v.Impact, v.Description, v.Help, v.HelpUrl,
                v.Nodes.Select(node => node.Target?.ToString() ?? string.Empty).ToList())));

            var blocking = result.Violations
                .Where(v => v.Impact is not null
                    && BlockingImpacts.Contains(v.Impact, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (blocking.Count == 0)
                continue;

            var message = BuildFailureMessage(path, blocking);
            failures.Add(message);
            var diagnosticsDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "e2e-failures");
            Directory.CreateDirectory(diagnosticsDirectory);
            var pageName = path == "/" ? "dashboard" : path.Trim('/').Replace('/', '-');
            File.WriteAllText(Path.Combine(diagnosticsDirectory, $"accessibility-violations-{pageName}.txt"), message);
            TestContext.Error.WriteLine(message);
        }

        WriteSarif(violations);
        AssertNoBlockingViolation(failures);
    }

    /// <summary>
    /// A content-sized grid has its rows hidden until OE has measured it (up to 5 s on a loaded agent), and its
    /// viewport used to keep scrolling over those hidden rows: a scroll region with nothing a keyboard
    /// can reach (scrollable-region-focusable). The dashboard calls are held so the scan lands on that
    /// window, whatever the speed of the machine, and the test checks the tables really are hidden then.
    /// </summary>
    [Test]
    public async Task Accessibility_DashboardWhileItsGridsLoad_HasNoSeriousOrCriticalViolations()
    {
        await NavigateToAsync("/");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pattern = new System.Text.RegularExpressions.Regex(@"/api/(monitoring/dashboard|appmonitoring/summary)(\?|$)");
        await Page.RouteAsync(pattern, async route =>
        {
            await release.Task;
            await route.ContinueAsync();
        });
        try
        {
            await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var bars = Page.Locator(".dashboard-tile thead > tr.omni-data-grid__progress .omni-loading-bar--active");
            await Expect(bars).ToHaveCountAsync(4, new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });

            var hiddenTables = Page.Locator(".dashboard-tile table.omni-data-grid__table[data-omni-sizing='content']:not([data-omni-sized])");
            var hiddenBefore = await HiddenTableCountAsync();
            var result = await Page.RunAxe(ScanOptions);
            var hiddenAfter = await HiddenTableCountAsync();

            // The window is the one under test: all four tables hidden before and after the scan.
            Assert.That(new[] { hiddenBefore, hiddenAfter }, Is.EqualTo(new[] { 4, 4 }),
                "The scan did not land while the four dashboard tables were hidden.");
            var blocking = result.Violations
                .Where(v => v.Impact is not null && BlockingImpacts.Contains(v.Impact, StringComparer.OrdinalIgnoreCase))
                .ToList();
            AssertNoBlockingViolation(blocking.Count == 0 ? [] : [BuildFailureMessage("/ (grids loading)", blocking)]);

            // OE 1.10.0 hides the rows of such a table (tbody, tfoot) and keeps the table itself, with its
            // progress bar, visible; OE 1.9.4 hid the whole table. The rows are what a user cannot reach.
            async Task<int> HiddenTableCountAsync() => await hiddenTables.EvaluateAllAsync<int>(
                "tables => tables.filter(table => { const rows = table.querySelector(':scope > tbody, :scope > tfoot'); "
                + "return rows !== null && getComputedStyle(rows).visibility === 'hidden'; }).length");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>The pages a user works in, each scanned once per run.</summary>
    private static readonly string[] MainPages =
        ["/", "/servers", "/projects", "/pipelines", "/releases", "/analysis", "/environments"];

    /// <summary>
    /// Into the QA results directory when the runner names it (AETHEUS_E2E_RESULTS_DIR, which the
    /// QA script copies out of the container), else beside the test output.
    /// </summary>
    private static void WriteSarif(IReadOnlyCollection<AccessibilityViolation> violations)
    {
        var directory = Environment.GetEnvironmentVariable("AETHEUS_E2E_RESULTS_DIR") is { Length: > 0 } results
            ? results
            : Path.Combine(TestContext.CurrentContext.WorkDirectory, "e2e-results");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, AccessibilitySarif.FileName), AccessibilitySarif.Build(violations));
        TestContext.Out.WriteLine($"Accessibility: {violations.Count} violation(s) written to {AccessibilitySarif.FileName}.");
    }

    /// <summary>Serious or critical violations fail the release under test. Against the deployed
    /// baseline's images (E2E_PREVIOUS_PAYLOAD_ONLY=true, the V-1 gate of a candidate) the pages still
    /// have to load (rendering and 5xx checks of <see cref="E2ETestBase"/>), but a violation is the
    /// baseline's own defect, one the candidate may be the fix of: it is logged, not failed (owner
    /// decision of 2026-10-29, candidate #3010 refused on a contrast defect develop had fixed).</summary>
    private static void AssertNoBlockingViolation(IReadOnlyList<string> failures)
    {
        if (failures.Count > 0 && NewSinceDeployedBaselineAttribute.TargetIsPayloadOnlyPrevious)
        {
            TestContext.Out.WriteLine("Accessibility violations of the deployed baseline, reported without failing its rollback gate:\n"
                + string.Join("\n\n", failures));
            return;
        }
        Assert.That(failures, Is.Empty, () => string.Join("\n\n", failures));
    }

    private static string BuildFailureMessage(string path, IReadOnlyList<AxeResultItem> blocking)
    {
        var lines = blocking.Select(v =>
        {
            var targets = string.Join("\n", v.Nodes.Take(5).Select(n =>
                $"    target: {n.Target}\n    html: {n.Html}"));
            return $"  [{v.Impact}] {v.Id}: {v.Description} (help: {v.Help}) - {v.Nodes.Length} node(s)"
                 + (string.IsNullOrEmpty(targets) ? "" : $"\n{targets}");
        });

        return $"Page '{path}' has {blocking.Count} serious/critical WCAG accessibility violation(s):\n"
             + string.Join("\n", lines);
    }
}
