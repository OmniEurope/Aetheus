// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// Opens each page of the inventory in an application that is already booted and measures it with
/// <see cref="PageRuleScripts.Measure"/>. A page is read twice with a quiet window in between and only what both reads
/// show counts, so a late render never turns into a violation that comes and goes between two runs.
/// </summary>
internal sealed class PageRuleSweeper(Func<string, Task> openWithFullLoad)
{
    public const string UnreachableRule = "0.page-unreachable";
    public const string NeverSettlesRule = "0.page-never-settles";
    public const string RowHeightRule = "3.row-height-differs";

    private const int SettleBudgetMs = 8000;
    private const int SettleRetryBudgetMs = 8000;
    private const double RowHeightToleranceInPixels = 1;

    /// <summary>Navigates to the page of <paramref name="target"/> through the router and measures it.</summary>
    public async Task<PageMeasurement> MeasureAsync(IPage page, PageTarget target, int width)
    {
        var clock = Stopwatch.StartNew();
        var path = target.Path ?? throw new ArgumentException("An unreachable target cannot be measured.", nameof(target));
        try
        {
            await OpenAsync(page, path);
        }
        catch (PlaywrightException exception)
        {
            return NotMeasured(target.Template, width, clock, "could not be opened: " + FirstLine(exception.Message));
        }
        return await MeasureLoadedAsync(page, target.Template, path, width, clock);
    }

    /// <summary>Measures the page that is already on screen, for the page whose address is <paramref name="requestedPath"/>.</summary>
    public async Task<PageMeasurement> MeasureLoadedAsync(IPage page, string template, string requestedPath, int width, Stopwatch? clock = null)
    {
        clock ??= Stopwatch.StartNew();
        try
        {
            if (await SettleAsync(page) is { } notSettled)
                return NeverSettled(page, template, width, clock, notSettled);
            var first = await ReadAsync(page, template, width);
            if (await SettleAsync(page) is { } changedAgain)
                return NeverSettled(page, template, width, clock, changedAgain);
            var second = await ReadAsync(page, template, width);

            var redirect = Normalise(second.Path) == Normalise(requestedPath) ? null : PageInventory.TemplateOf(second.Path);
            var secondKeys = second.Violations.Select(violation => violation.Key).ToHashSet(StringComparer.Ordinal);
            var firstKeys = first.Violations.Select(violation => violation.Key).ToHashSet(StringComparer.Ordinal);
            var stable = second.Violations.Where(violation => firstKeys.Contains(violation.Key)).ToList();
            var intermittent = first.Violations.Where(violation => !secondKeys.Contains(violation.Key))
                .Concat(second.Violations.Where(violation => !firstKeys.Contains(violation.Key)))
                .Select(violation => $"{violation.Rule} {violation.Element}")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return new PageMeasurement(template, width, second.Path, stable, second.Grids, intermittent, null, redirect, null,
                (int)clock.ElapsedMilliseconds);
        }
        catch (PlaywrightException exception)
        {
            return NotMeasured(template, width, clock, "could not be measured: " + FirstLine(exception.Message));
        }
    }

    /// <summary>
    /// Turns the measurements into the violations of the sweep: the ones measured, the row height that differs between
    /// the two widths, and one <see cref="UnreachableRule"/> entry for each page that could not be put on screen.
    /// </summary>
    public static IReadOnlyList<PageRuleViolation> Violations(IReadOnlyList<PageTarget> targets, IReadOnlyList<PageMeasurement> measurements)
    {
        var violations = new List<PageRuleViolation>();
        var redirected = measurements
            .Where(measurement => measurement.RedirectedTo is not null)
            .GroupBy(measurement => measurement.Page, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().RedirectedTo!, StringComparer.Ordinal);

        foreach (var target in targets.Where(target => target.UnreachableReason is not null))
            violations.Add(new PageRuleViolation(target.Template, 0, UnreachableRule, target.UnreachableReason!, target.UnreachableReason!));
        foreach (var (page, destination) in redirected)
            violations.Add(new PageRuleViolation(page, 0, UnreachableRule, "redirects to " + destination, "redirects to " + destination));
        foreach (var unsettled in measurements.Where(measurement => measurement.Unsettled is not null && !redirected.ContainsKey(measurement.Page)))
            violations.Add(new PageRuleViolation(unsettled.Page, unsettled.Width, NeverSettlesRule, unsettled.Unsettled!.Element, unsettled.Unsettled.Detail));

        var measured = measurements.Where(measurement => measurement.Measured && !redirected.ContainsKey(measurement.Page)).ToList();
        violations.AddRange(measured.SelectMany(measurement => measurement.Violations));
        foreach (var page in measured.GroupBy(measurement => measurement.Page, StringComparer.Ordinal))
        {
            var wide = page.FirstOrDefault(measurement => measurement.Width > 600);
            var narrow = page.FirstOrDefault(measurement => measurement.Width <= 600);
            if (wide is null || narrow is null) continue;
            foreach (var grid in narrow.Grids.Where(grid => grid.Rows > 0))
            {
                var counterpart = wide.Grids.FirstOrDefault(other => other.Grid == grid.Grid && other.Rows > 0);
                if (counterpart is not null && Math.Abs(counterpart.RowHeight - grid.RowHeight) > RowHeightToleranceInPixels)
                    violations.Add(new PageRuleViolation(page.Key, narrow.Width, RowHeightRule, grid.Grid,
                        $"{counterpart.RowHeight} px at {wide.Width} px, {grid.RowHeight} px at {narrow.Width} px"));
            }
        }
        return violations;
    }

    private async Task OpenAsync(IPage page, string path)
    {
        try
        {
            await page.EvaluateAsync("path => window.Blazor.navigateTo(path)", path);
            // The router renders the route in the .NET call that follows the history change: two frames later its first
            // render batch, loaders included, is in the document.
            await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        }
        catch (PlaywrightException)
        {
            // A page that forces a document load destroys the evaluation context, and a document that has not booted has
            // no router: open it the way a visitor typing the address would, which waits for the application to be ready.
            await openWithFullLoad(path);
        }
    }

    /// <summary>Waits for the page to stop changing; null when it did, else what kept it from settling.</summary>
    private static async Task<PageUnsettled?> SettleAsync(IPage page)
    {
        var result = await page.EvaluateAsync<JsonElement>(PageRuleScripts.Settle, SettleBudgetMs);
        if (result.GetProperty("settled").GetBoolean()) return null;
        result = await page.EvaluateAsync<JsonElement>(PageRuleScripts.Settle, SettleRetryBudgetMs);
        if (result.GetProperty("settled").GetBoolean()) return null;
        var busyOn = result.GetProperty("busyOn").GetString();
        var element = string.IsNullOrEmpty(busyOn) ? "the page keeps changing size" : $"a loader stays on screen: {busyOn}";
        return new PageUnsettled(element,
            $"{element} ({result.GetProperty("changes").GetInt32()} size changes in the last wait, signature {result.GetProperty("signature").GetString()})");
    }

    private static PageMeasurement NeverSettled(IPage page, string template, int width, Stopwatch clock, PageUnsettled unsettled) =>
        new(template, width, new Uri(page.Url).AbsolutePath, [], [], [], null, null, unsettled, (int)clock.ElapsedMilliseconds);

    private static async Task<Reading> ReadAsync(IPage page, string template, int width)
    {
        var json = await page.EvaluateAsync<JsonElement>(PageRuleScripts.Measure);
        var violations = json.GetProperty("violations").EnumerateArray()
            .Select(item => new PageRuleViolation(
                template,
                width,
                item.GetProperty("rule").GetString()!,
                item.GetProperty("element").GetString()!,
                item.GetProperty("detail").GetString() ?? string.Empty))
            .ToList();
        var grids = json.GetProperty("grids").EnumerateArray()
            .Select(item => new PageGridFact(
                item.GetProperty("grid").GetString()!,
                item.GetProperty("rowHeight").GetDouble(),
                item.GetProperty("rows").GetInt32()))
            .ToList();
        return new Reading(json.GetProperty("path").GetString()!, violations, grids);
    }

    private static PageMeasurement NotMeasured(string template, int width, Stopwatch clock, string reason) =>
        new(template, width, string.Empty, [], [], [], reason, null, null, (int)clock.ElapsedMilliseconds);

    private static string Normalise(string path)
    {
        var trimmed = path.Split('?', '#')[0].TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed.ToLowerInvariant();
    }

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    private sealed record Reading(string Path, IReadOnlyList<PageRuleViolation> Violations, IReadOnlyList<PageGridFact> Grids);
}
