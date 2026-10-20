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
        Assert.That(failures, Is.Empty, () => string.Join("\n\n", failures));
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
