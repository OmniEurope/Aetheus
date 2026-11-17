// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// Recette R-22. Summary tiles above a grid pushed the grid under the fold of a phone: on /pipelines/24 at
/// 375 x 650 the four run tiles took 436 px, the Executions grid started at y=654 and showed 0 px. These tests
/// measure what a visitor sees, not the DOM: the part of the grid that is inside the window and inside every
/// scrolling ancestor (the content area, a tab panel), and how many data rows are wholly inside that part.
/// </summary>
[Category("E2E")]
[Category("MobileGrid")]
[NonParallelizable]
public sealed class MobileGridVisibilityTests : E2ETestBase
{
    /// <summary>Visible height (px) of the first data grid of a page on a phone: a header and about four rows.</summary>
    private const double VisibleHeightFloor = 200;

    /// <summary>A page whose grid holds seeded rows shows at least this many of them on the first screen.</summary>
    private const int VisibleRowsFloor = 3;

    protected override bool ShareBrowserContextAcrossTests => true;

    // For each visible data grid, in document order: where it starts, how tall it is, how much of it is visible
    // (inside the window and inside every scrolling ancestor), and how many data rows are wholly in that part.
    private const string MeasureGrids = """
        () => {
          const vh = window.innerHeight;
          const band = (el) => {
            let top = 0, bottom = vh;
            for (let p = el.parentElement; p && p !== document.documentElement; p = p.parentElement) {
              if (/(auto|scroll|hidden|clip)/.test(getComputedStyle(p).overflowY)) {
                const r = p.getBoundingClientRect();
                top = Math.max(top, r.top);
                bottom = Math.min(bottom, r.bottom);
              }
            }
            return { top, bottom };
          };
          return [...document.querySelectorAll('.omni-data-grid')]
            .filter(grid => { const r = grid.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(grid).visibility !== 'hidden'; })
            .map(grid => {
              const r = grid.getBoundingClientRect();
              const { top, bottom } = band(grid);
              const rows = [...grid.querySelectorAll('tbody tr[data-omni-row-index]')];
              const whole = rows.filter(row => { const q = row.getBoundingClientRect(); return q.height > 0 && q.top >= top - 1 && q.bottom <= bottom + 1; });
              return {
                name: [...grid.classList].filter(c => !/^omni-/.test(c)).join('.') || 'grid',
                top: Math.round(r.top),
                height: Math.round(r.height),
                visible: Math.round(Math.max(0, Math.min(r.bottom, bottom) - Math.max(r.top, top))),
                rows: rows.length,
                rowsVisible: whole.length,
                // What stacks above the grid: the earlier siblings of the grid and of each ancestor, with their size.
                above: (() => {
                  const found = [];
                  for (let node = grid; node && node.id !== 'main-content' && node !== document.body; node = node.parentElement)
                    for (let sibling = node.previousElementSibling; sibling; sibling = sibling.previousElementSibling) {
                      const q = sibling.getBoundingClientRect();
                      if (q.height > 0) found.unshift(sibling.tagName.toLowerCase() + [...sibling.classList].slice(0, 2).map(c => '.' + c).join('') + ' ' + Math.round(q.height));
                    }
                  return found.join(' | ');
                })()
              };
            });
        }
        """;

    private sealed record GridView(string Name, double Top, double Height, double Visible, int Rows, int RowsVisible, string Above)
    {
        public override string ToString() =>
            $"{Name} top={Top.ToString(CultureInfo.InvariantCulture)} height={Height.ToString(CultureInfo.InvariantCulture)} "
            + $"visible={Visible.ToString(CultureInfo.InvariantCulture)} rows={Rows} rowsVisible={RowsVisible} above=[{Above}]";
    }

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    private async Task<bool> WaitUntilSettledAsync()
    {
        var result = await Page.EvaluateAsync<JsonElement>(PageRuleScripts.Settle, 10000);
        return result.GetProperty("settled").GetBoolean();
    }

    private async Task<IReadOnlyList<GridView>> GridsAsync()
    {
        var json = await Page.EvaluateAsync<JsonElement>(MeasureGrids);
        return json.EnumerateArray().Select(item => new GridView(
            item.GetProperty("name").GetString()!,
            item.GetProperty("top").GetDouble(),
            item.GetProperty("height").GetDouble(),
            item.GetProperty("visible").GetDouble(),
            item.GetProperty("rows").GetInt32(),
            item.GetProperty("rowsVisible").GetInt32(),
            item.GetProperty("above").GetString()!)).ToList();
    }

    [TestCase(375, 650)]
    [TestCase(375, 812)]
    [Category("Pipelines")]
    public async Task PipelineRunsGrid_KeepsAUsefulVisibleHeightOnAPhone(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        // The seeded pipelines each carry three runs, so the Executions tab holds the four summary tiles and a grid.
        await NavigateToAsync("/pipelines/1");
        await Expect(Page.Locator(".pipeline-runs-grid")).ToBeVisibleAsync();
        Assert.That(await WaitUntilSettledAsync(), Is.True, "The pipeline page never settled.");

        var tilesBottom = await Page.Locator(".pipeline-runs-summary").EvaluateAsync<double>("element => element.getBoundingClientRect().bottom");
        var grid = (await GridsAsync()).FirstOrDefault(item => item.Name.Contains("pipeline-runs-grid", StringComparison.Ordinal));
        TestContext.Out.WriteLine($"[R-22] /pipelines/1 at {width}x{height}: tiles bottom={tilesBottom}; {grid}");

        Assert.That(grid, Is.Not.Null, "The runs grid is not on the Executions tab.");
        Assert.Multiple(() =>
        {
            Assert.That(grid!.Visible, Is.GreaterThanOrEqualTo(VisibleHeightFloor),
                $"The runs grid shows {grid.Visible} px of {grid.Height} px (it starts at y={grid.Top}).");
            Assert.That(grid.Rows, Is.GreaterThanOrEqualTo(VisibleRowsFloor), "The seeded pipeline has no runs to show.");
            Assert.That(grid.RowsVisible, Is.GreaterThanOrEqualTo(VisibleRowsFloor),
                $"Only {grid.RowsVisible} of {grid.Rows} runs are wholly visible on the first screen.");
        });
    }

    /// <summary>
    /// Pages whose single grid is not the page's content, so the lone-grid rule (OEAPP-08/09) does not size them: the
    /// form is the content and the grid only summarizes it (user creation), or the grid is one widget of an overview
    /// that scrolls (project overview). Each entry names its reason; none is a page that is only a grid.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotALoneGridPage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/users/new"] = "creation form first, the grid summarizes the roles picked above it",
        ["/admin/users/new"] = "creation form first, the grid summarizes the roles picked above it",
        ["/projects/{Id:int}/overview"] = "overview of cards and widgets, the recent-runs grid is one of them",
    };

    [TestCase(375, 650)]
    [TestCase(375, 812)]
    public async Task EveryLoneGridPage_ShowsItsGridOnTheFirstScreenOfAPhone(int width, int height)
    {
        // The sweep opens about eighty pages in a row, each starting its SignalR hubs, and runs under a loaded host.
        // Measured once (2026-11-07, 23 such errors in one run, none in the runs around it). Likely cause, read from the code,
        // not reproduced: a hub that was negotiated (POST .../negotiate answered) but whose WebSocket reached the back more than
        // DisconnectTimeout (15 s, the default: Program.cs does not set it) later is answered 404 because the back
        // already dropped the never-attached connection (the back classifies the same 404 as the expected end of a
        // stale transport, RequestErrorLoggingMiddleware.IsStaleHubTransport); the browser then writes this console
        // error itself, and the app reconnects. It says nothing about the layout this test measures. Only that exact
        // message is allowed: a hub route (any hub: the sweep also hit entities), the transport request (it carries the connection id, so a 404 on a negotiate
        // or on a wrong hub route still fails), and the 404 status. Any other console or page error fails the test.
        AllowBrowserDiagnostic(new Regex(
            @"^console error: WebSocket connection to 'wss?://[^/']+/hubs/[a-z]+\?id=[^'&]+(?:&access_token=[^']*)?' failed: Error during WebSocket handshake: Unexpected response code: 404",
            RegexOptions.CultureInvariant));
        await Page.SetViewportSizeAsync(width, height);
        await NavigateToAsync("/");
        var entities = await new PageEntityResolver(Page, BackendUrl).ResolveAsync();
        var targets = PageInventory.Resolve(entities)
            .Where(target => target.UnreachableReason is null && target.Path is not null && target.Template != "/login")
            .ToList();

        var measured = new List<(string Page, int GridCount, GridView First)>();
        var notSettled = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                await Page.EvaluateAsync("path => window.Blazor.navigateTo(path)", target.Path);
            }
            catch (PlaywrightException)
            {
                await NavigateToAsync(target.Path!);
            }
            if (!await WaitUntilSettledAsync())
            {
                notSettled.Add(target.Template);
                continue;
            }
            // The page may have redirected (a page the account cannot open); only the page asked for counts.
            if (!string.Equals(new Uri(Page.Url).AbsolutePath, target.Path, StringComparison.OrdinalIgnoreCase)) continue;
            var grids = await GridsAsync();
            if (grids.Count > 0)
                measured.Add((target.Template, grids.Count, grids[0]));
        }

        foreach (var (page, count, first) in measured.OrderBy(item => item.First.Visible))
            TestContext.Out.WriteLine($"[R-22] {page} at {width}x{height}: grids={count} {first}");
        TestContext.Out.WriteLine($"[R-22] pages measured with a grid: {measured.Count}; not settled (not measured): {string.Join(", ", notSettled)}");

        // Several grids on one page is the rule's exception: only a page that is one grid is held to the floor. An empty
        // grid is held to the height too (it is the same box), but the rows are checked only when the data has them.
        var hidden = measured
            .Where(item => item.GridCount == 1 && !NotALoneGridPage.ContainsKey(item.Page))
            .Where(item => item.First.Visible < Math.Min(VisibleHeightFloor, item.First.Height) - 1
                || (item.First.Rows >= VisibleRowsFloor && item.First.RowsVisible < VisibleRowsFloor))
            .Select(item => $"{item.Page}: {item.First}")
            .ToList();
        Assert.That(hidden, Is.Empty, $"Lone-grid pages whose grid shows less than {VisibleHeightFloor} px or fewer than {VisibleRowsFloor} rows on the first screen at {width}x{height}:\n  "
            + string.Join("\n  ", hidden));
    }
}
