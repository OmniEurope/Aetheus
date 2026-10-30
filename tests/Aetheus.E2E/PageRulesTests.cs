// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// Recette R-30: every page of <c>Aetheus.Front</c> is opened at 1280 px and at 375 px and measured against the
/// presentation rules that can be measured objectively (the primary action in the page header, the gap between blocks,
/// grids without a cut value, text that wraps only when it has no room, no horizontal overflow, no coloured side
/// stripe). The violations that exist today are checked in as a baseline and the run fails on any difference: a new
/// violation, a violation that was fixed and is still listed, or a page that could not be measured. The pages are chosen
/// by the smallest identifier of each entity, so the rows other tests create never change which page is measured.
/// </summary>
[Category("E2E")]
[Category("PageRules")]
[NonParallelizable]
public sealed class PageRulesTests : E2ETestBase
{
    private const string OutputVariable = "AETHEUS_PAGE_RULES_OUTPUT";
    private const string SweepVariable = "AETHEUS_PAGE_RULES_SWEEP";
    private const string DirectivesResource = "front-page-directives.txt";
    private const string LoginTemplate = "/login";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly (int Width, int Height)[] Viewports = [(1280, 800), (375, 812)];
    private static readonly Regex PageDirective = new(@"^@page\s+""(?<route>[^""]+)""\s*$", RegexOptions.Compiled);

    protected override bool ShareBrowserContextAcrossTests => true;

    [Test]
    public void Inventory_lists_exactly_the_page_directives_of_the_front()
    {
        var captured = ReadCapturedDirectives();
        var inventoried = PageInventory.All.Select(route => route.Template).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(captured, Is.Not.Empty, "The build captured no @page directive from src/Aetheus.Front.");
            Assert.That(captured.Except(inventoried, StringComparer.Ordinal), Is.Empty,
                "Pages of the front that PageInventory does not list: add them, with the entity that fills their parameters.");
            Assert.That(inventoried.Except(captured, StringComparer.Ordinal), Is.Empty,
                "Pages that PageInventory lists and the front no longer routes: remove them.");
            Assert.That(inventoried.Order(StringComparer.Ordinal).ToList(), Is.EqualTo(captured.Order(StringComparer.Ordinal).ToList()),
                "A route is declared a different number of times in the front and in PageInventory.");
        });
    }

    [Test]
    public void Ratchet_flags_a_new_violation_and_a_baseline_line_that_no_longer_occurs()
    {
        var baseline = new HashSet<string>(StringComparer.Ordinal) { "/a | 375 | 3.grid-header-wrapped | grid th[Name]", "/b | 1280 | 2.blocks-touching | x / y" };
        var actual = new[] { "/b | 1280 | 2.blocks-touching | x / y", "/c | 375 | 5.page-overflow | document" };

        var ratchet = PageRuleBaseline.Compare(baseline, actual);

        Assert.Multiple(() =>
        {
            Assert.That(ratchet.New, Is.EqualTo(new[] { "/c | 375 | 5.page-overflow | document" }), "A violation outside the baseline is a regression.");
            Assert.That(ratchet.Stale, Is.EqualTo(new[] { "/a | 375 | 3.grid-header-wrapped | grid th[Name]" }), "A baseline line that no longer occurs must be deleted.");
            Assert.That(ratchet.DataNew, Is.Empty);
            Assert.That(ratchet.DataGone, Is.Empty);
            Assert.That(PageRuleBaseline.Compare(baseline, baseline).IsClean, Is.True, "The same set on both sides is clean.");
        });
    }

    [Test]
    public void Ratchet_reports_without_failing_the_differences_of_a_rule_that_depends_on_the_rows_on_screen()
    {
        var baseline = new HashSet<string>(StringComparer.Ordinal) { "/a | 375 | 3.grid-cell-truncated | grid td[Name]" };
        var actual = new[] { "/c | 1280 | 3.grid-cell-truncated | grid td[Details]" };

        var ratchet = PageRuleBaseline.Compare(baseline, actual);

        Assert.Multiple(() =>
        {
            Assert.That(ratchet.IsClean, Is.True, "A cut cell appears or disappears with the rows other tests create: it must not fail the run.");
            Assert.That(ratchet.DataNew, Is.EqualTo(new[] { "/c | 1280 | 3.grid-cell-truncated | grid td[Details]" }), "The report still lists the cut cell the baseline does not know.");
            Assert.That(ratchet.DataGone, Is.EqualTo(new[] { "/a | 375 | 3.grid-cell-truncated | grid td[Name]" }), "The report still lists the baseline line that did not occur.");
            Assert.That(PageRuleBaseline.IsDataDependent("/x | 375 | 3.grid-height | grid"), Is.False, "Only the rules of the data-dependent set escape the gate.");
        });
    }

    [Test]
    public async Task Every_page_is_measured_and_its_violations_match_the_baseline()
    {
        // Owner decision of 2026-10-27: the sweep of every page is run by hand, from time to time (ylaunch -tec PageRules
        // sets the variable), never by -ta nor by the candidate's QA, whose agent accepts no category filter.
        if (Environment.GetEnvironmentVariable(SweepVariable) != "1")
            Assert.Ignore($"The sweep of every page runs by hand only: ylaunch -tec PageRules ({SweepVariable}=1).");
        var clock = Stopwatch.StartNew();
        await NavigateToAsync("/");
        var entities = await new PageEntityResolver(Page, BackendUrl).ResolveAsync();
        var targets = PageInventory.Resolve(entities);
        var sweeper = new PageRuleSweeper(NavigateToAsync);

        var measurements = new List<PageMeasurement>();
        foreach (var (width, height) in Viewports)
        {
            await Page.SetViewportSizeAsync(width, height);
            await NavigateToAsync("/");
            foreach (var target in targets.Where(target => target.UnreachableReason is null && target.Template != LoginTemplate))
                measurements.Add(await sweeper.MeasureAsync(Page, target, width));
            measurements.Add(await MeasureLoginAsync(sweeper, width, height));
        }

        var violations = PageRuleSweeper.Violations(targets, measurements);
        var sweep = new PageRuleSweep(targets, measurements, violations, clock.Elapsed);
        var ratchet = PageRuleBaseline.Compare(PageRuleBaseline.Load(), violations.Select(violation => violation.Key));
        var widths = Viewports.Select(viewport => viewport.Width).ToList();
        var output = WriteOutputs(sweep, widths, ratchet);
        TestContext.Out.WriteLine(Summary(sweep, widths, ratchet, output));

        var notMeasured = measurements.Where(measurement => measurement.NotMeasuredReason is not null)
            .Select(measurement => $"{measurement.Page} at {measurement.Width} px {measurement.NotMeasuredReason}").ToList();
        var absent = (from target in targets
                      where target.UnreachableReason is null
                      from width in widths
                      where !measurements.Any(measurement => measurement.Page == target.Template && measurement.Width == width)
                      select $"{target.Template} at {width} px").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(absent, Is.Empty, "Inventoried pages that were not even attempted:\n  " + string.Join("\n  ", absent));
            Assert.That(notMeasured, Is.Empty, "Pages that could not be measured (never baselined):\n  " + string.Join("\n  ", notMeasured));
            Assert.That(ratchet.New, Is.Empty,
                $"Violations that are not in the baseline (a regression, or a page to fix). Report and proposed baseline: {output}\n  "
                + string.Join("\n  ", ratchet.New));
            Assert.That(ratchet.Stale, Is.Empty,
                $"Baseline lines that no longer occur (a fix: delete them from {PageRuleBaseline.SourcePath}). Proposed baseline: {output}\n  "
                + string.Join("\n  ", ratchet.Stale));
        });
    }

    // The login page redirects away when the session is signed in: it is measured in a context without a session.
    private async Task<PageMeasurement> MeasureLoginAsync(PageRuleSweeper sweeper, int width, int height)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = width, Height = height }
        });
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{FrontendUrl}{LoginTemplate}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForSelectorAsync("#Email", new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
            return await sweeper.MeasureLoadedAsync(page, LoginTemplate, LoginTemplate, width);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static IReadOnlyList<string> ReadCapturedDirectives()
    {
        using var stream = typeof(PageRulesTests).Assembly.GetManifestResourceStream(DirectivesResource)
            ?? throw new InvalidOperationException($"The resource '{DirectivesResource}' is missing: the project file must capture the @page directives of the front.");
        using var reader = new StreamReader(stream, Utf8);
        var routes = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            var match = PageDirective.Match(line.Trim());
            if (match.Success) routes.Add(match.Groups["route"].Value);
        }
        return routes;
    }

    private static string WriteOutputs(PageRuleSweep sweep, IReadOnlyList<int> widths, PageRuleRatchet ratchet)
    {
        var configured = Environment.GetEnvironmentVariable(OutputVariable);
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(TestContext.CurrentContext.WorkDirectory, "page-rules")
            : configured;
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "page-rules-report.md"), PageRuleReport.Build(sweep, widths, ratchet), Utf8);
        File.WriteAllText(Path.Combine(directory, "page-rules-baseline.proposed.txt"),
            PageRuleBaseline.Format(sweep.Violations.Select(violation => violation.Key)), Utf8);

        var lines = sweep.Measurements
            .OrderBy(measurement => measurement.Page, StringComparer.Ordinal).ThenBy(measurement => measurement.Width)
            .Select(measurement => string.Join('\t',
                measurement.Page, measurement.Width.ToString(CultureInfo.InvariantCulture), measurement.FinalPath,
                Status(measurement),
                measurement.Milliseconds.ToString(CultureInfo.InvariantCulture) + " ms",
                measurement.Intermittent.Count.ToString(CultureInfo.InvariantCulture) + " intermittent"));
        File.WriteAllLines(Path.Combine(directory, "page-rules-measurements.tsv"), lines, Utf8);
        return directory;
    }

    private static string Status(PageMeasurement measurement) =>
        measurement.NotMeasuredReason is not null ? "not measured: " + measurement.NotMeasuredReason
        : measurement.RedirectedTo is not null ? "redirected to " + measurement.RedirectedTo
        : measurement.Unsettled is not null ? "never settles: " + measurement.Unsettled.Detail
        : "measured";

    private static string Summary(PageRuleSweep sweep, IReadOnlyList<int> widths, PageRuleRatchet ratchet, string output)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"[PAGE-RULES] {sweep.Targets.Count} pages inventoried, sweep took {sweep.Elapsed:mm\\:ss}, output in {output}");
        foreach (var width in widths)
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"[PAGE-RULES] measured at {width} px: {sweep.Measurements.Count(measurement => measurement.Width == width && measurement.Measured)}");
        foreach (var group in sweep.Violations.GroupBy(violation => violation.Rule).OrderBy(group => group.Key, StringComparer.Ordinal))
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"[PAGE-RULES] {group.Key}: {group.Count()} violations on {group.Select(violation => violation.Page).Distinct().Count()} pages");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"[PAGE-RULES] against the baseline: {ratchet.New.Count} new, {ratchet.Stale.Count} stale; reported without failing (data-dependent rule): {ratchet.DataNew.Count} new, {ratchet.DataGone.Count} not seen");
        return builder.ToString().TrimEnd();
    }
}
