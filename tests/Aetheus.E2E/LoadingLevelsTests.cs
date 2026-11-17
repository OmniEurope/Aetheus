// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// R-36: waiting is veiled at three nested levels (site splash, content-area veil, one component's veil), never with two
/// logos at once, and the plane keeps its size and, from the splash to the content area, its place. The proof is a slow
/// load in a real browser: API answers are held back, and a sampler running in the page (every animation frame, from the
/// first script of the document) records every plane that is drawn: which layer owns it, its box and its opacity.
/// </summary>
[Category("E2E")]
[Category("LoadingLevels")]
[Category("Dashboard")]
public sealed class LoadingLevelsTests : E2ETestBase
{
    // Read in the page from the first script of each document; one entry per animation frame in which a plane is drawn.
    private const string Sampler = """
        (() => {
            window.__planeSamples = [];
            const owner = el => el.closest('#app-splash') ? 'splash'
                : el.closest('.site-content-veil') ? 'content'
                : el.closest('.zone-veil-host') ? 'zone'
                : el.closest('.aetheus-loader, .omni-logo-loader') ? 'loader'
                : 'other';
            const effectiveOpacity = el => {
                let opacity = 1;
                for (let node = el; node && node.nodeType === 1; node = node.parentElement) {
                    const style = getComputedStyle(node);
                    if (style.display === 'none' || style.visibility === 'hidden') return 0;
                    opacity *= parseFloat(style.opacity);
                }
                return opacity;
            };
            const tick = () => {
                const planes = [];
                for (const el of document.querySelectorAll('svg.app-splash-logo, svg.aetheus-loader-logo, .omni-logo-loader')) {
                    const rect = el.getBoundingClientRect();
                    const opacity = effectiveOpacity(el);
                    if (rect.width === 0 || rect.height === 0 || opacity < 0.05) continue;
                    planes.push({ owner: owner(el), x: rect.x + rect.width / 2, y: rect.y + rect.height / 2,
                                  w: rect.width, h: rect.height, opacity: Math.round(opacity * 100) / 100,
                                  leaving: !!el.closest('.app-splash--hidden') });
                }
                window.__planeSamples.push({ t: Math.round(performance.now()), planes });
                requestAnimationFrame(tick);
            };
            requestAnimationFrame(tick);
        })();
        """;

    private sealed record Plane(string Owner, double X, double Y, double W, double H, double Opacity, bool Leaving);

    private sealed record Sample(long T, List<Plane> Planes);

    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
        await Page.AddInitScriptAsync(Sampler);
    }

    private async Task<List<Sample>> ReadSamplesAsync()
    {
        var json = await Page.EvaluateAsync<JsonElement>("() => window.__planeSamples");
        return json.EnumerateArray().Select(sample => new Sample(
            sample.GetProperty("t").GetInt64(),
            sample.GetProperty("planes").EnumerateArray().Select(plane => new Plane(
                plane.GetProperty("owner").GetString()!,
                plane.GetProperty("x").GetDouble(),
                plane.GetProperty("y").GetDouble(),
                plane.GetProperty("w").GetDouble(),
                plane.GetProperty("h").GetDouble(),
                plane.GetProperty("opacity").GetDouble(),
                plane.GetProperty("leaving").GetBoolean())).ToList())).ToList();
    }

    private async Task HoldAsync(Regex pattern, int delayMs)
    {
        await Page.RouteAsync(pattern, async route =>
        {
            await Task.Delay(delayMs);
            try { await route.ContinueAsync(); }
            catch (PlaywrightException) { /* the page moved on while the answer was held */ }
        });
    }

    private static string Describe(List<Sample> samples)
    {
        // Run-length of the layers seen, in order: "splash x38, splash+content x7, content x30".
        var runs = new List<(string Key, int Count)>();
        foreach (var key in samples.Select(s => string.Join("+", s.Planes.Select(p => p.Owner + (p.Leaving ? "(leaving)" : "")).OrderBy(x => x))))
        {
            if (runs.Count > 0 && runs[^1].Key == key) runs[^1] = (key, runs[^1].Count + 1);
            else runs.Add((key, 1));
        }
        return string.Join(", ", runs.Select(r => $"[{(r.Key.Length == 0 ? "none" : r.Key)}] x{r.Count}"));
    }

    [Test]
    public async Task SlowSiteLoad_ShowsOneSplashThenOneContentVeil_TheSamePlaneInTheSamePlace()
    {
        // Every API answer is held 1.5 s: the session start (under the splash), then the page's own read (content veil).
        await HoldAsync(new Regex(@"/api/(?!client-errors)"), 1500);

        await Page.GotoAsync($"{FrontendUrl}/dashboards", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.WaitForFunctionAsync(
            """
            () => document.querySelector("[data-testid='app-body-wrapper']") !== null
                && !document.getElementById('app-splash')
                && document.querySelector('.site-content-veil') === null
                && !document.querySelector('.aetheus-loader')
            """,
            null, new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs * 2 });
        // A last frame, so a late layer would be sampled.
        await Page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");

        var samples = await ReadSamplesAsync();
        var drawn = samples.Where(s => s.Planes.Count > 0).ToList();
        var timeline = Describe(samples.SkipWhile(s => s.Planes.Count == 0).ToList());
        TestContext.Progress.WriteLine($"R-36 slow site load, plane layers per frame: {timeline}");

        var splash = drawn.SelectMany(s => s.Planes).Where(p => p.Owner == "splash").ToList();
        var content = drawn.SelectMany(s => s.Planes).Where(p => p.Owner == "content").ToList();
        Assert.That(splash, Is.Not.Empty, "The splash was never drawn: nothing was proved. " + timeline);
        Assert.That(content, Is.Not.Empty, "The content-area veil was never drawn (level 2): the slow read proved nothing. " + timeline);
        Assert.That(drawn.SelectMany(s => s.Planes).Where(p => p.Owner is "loader" or "other"), Is.Empty,
            "A logo outside the three levels was drawn (AetheusLoader / OmniLogoLoader). " + timeline);

        // Same size at both levels, same place (the middle of the window) from the splash to the content veil.
        // The size is read on planes that are fully drawn (the splash plane scales in from 80 % over its first 0.4 s,
        // and every plane fades with its veil); the centre is read on every plane, fading ones included.
        var reference = splash.First(p => p.Opacity >= 0.95 && !p.Leaving);
        foreach (var plane in splash.Concat(content))
        {
            Assert.That(plane.X, Is.EqualTo(reference.X).Within(1.5), $"{plane.Owner} plane x. {timeline}");
            Assert.That(plane.Y, Is.EqualTo(reference.Y).Within(1.5), $"{plane.Owner} plane y. {timeline}");
            if (plane.Opacity < 0.95) continue;
            Assert.That(plane.W, Is.EqualTo(reference.W).Within(1.0), $"{plane.Owner} plane width. {timeline}");
            Assert.That(plane.H, Is.EqualTo(reference.H).Within(1.0), $"{plane.Owner} plane height. {timeline}");
        }

        // Two planes in one frame only while the splash fades out over the content veil (a cross-fade of the same plane
        // in the same box); bounded by the splash's own 0.35 s transition plus its 0.6 s removal fallback.
        var overlapping = drawn.Where(s => s.Planes.Count > 1).ToList();
        var span = overlapping.Count == 0 ? 0 : overlapping.Last().T - overlapping.First().T;
        TestContext.Progress.WriteLine($"R-36 frames with two planes: {overlapping.Count} of {drawn.Count} drawn ({span} ms span)");
        Assert.That(overlapping.Where(s => s.Planes.Count != 2 || !s.Planes.Any(p => p.Leaving)), Is.Empty,
            "Two planes were drawn in one frame outside the splash's fade-out. " + timeline);
        Assert.That(span, Is.LessThanOrEqualTo(700), "The cross-fade of the splash over the content veil lasted too long. " + timeline);
        foreach (var frame in overlapping)
        {
            Assert.That(frame.Planes[0].X, Is.EqualTo(frame.Planes[1].X).Within(1.5), "Two planes at two places. " + timeline);
            Assert.That(frame.Planes[0].Y, Is.EqualTo(frame.Planes[1].Y).Within(1.5), "Two planes at two places. " + timeline);
            Assert.That(frame.Planes[0].W, Is.EqualTo(frame.Planes[1].W).Within(1.0), "Two planes of two sizes. " + timeline);
        }
    }

    [Test]
    public async Task SlowComponentLoad_ShowsOneVeilOverTheComponentOnly_AtTheSameSizeOfThePlane()
    {
        // The site is loaded without any delay, so the splash and the content-area veil are over before the component reads.
        await Page.GotoAsync($"{FrontendUrl}/admin/settings", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.WaitForFunctionAsync(
            "() => document.querySelector(\"[data-testid='app-body-wrapper']\") !== null && !document.getElementById('app-splash') && document.querySelector('.site-content-veil') === null",
            null, new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
        var splashSize = (await ReadSamplesAsync()).SelectMany(s => s.Planes).Where(p => p.Owner == "splash").Select(p => p.W).DefaultIfEmpty(0).Max();

        // Only now is the daily recap read held 3 s; opening its tab starts the read.
        await HoldAsync(new Regex(@"/api/settings/daily-recap(\?|$)"), 3000);
        await Page.EvaluateAsync("() => { window.__planeSamples.length = 0; }");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Daily recap" }).ClickAsync();
        await Page.WaitForSelectorAsync(".zone-veil-host", new() { State = WaitForSelectorState.Attached, Timeout = 10000 });
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.zone-veil-host') === null && document.querySelector('.daily-recap-settings') !== null",
            null, new() { Timeout = 30000 });
        await Page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");

        var samples = await ReadSamplesAsync();
        var timeline = Describe(samples.SkipWhile(s => s.Planes.Count == 0).ToList());
        TestContext.Progress.WriteLine($"R-36 slow component load, plane layers per frame: {timeline}");
        var planes = samples.SelectMany(s => s.Planes).ToList();

        Assert.That(planes.Where(p => p.Owner == "zone"), Is.Not.Empty, "The component veil (level 3) was never drawn. " + timeline);
        Assert.That(planes.Where(p => p.Owner != "zone"), Is.Empty,
            "A second plane (content-area veil, splash or loader) was drawn while one component waited. " + timeline);
        Assert.That(samples.Where(s => s.Planes.Count > 1), Is.Empty, "Two planes were drawn in one frame. " + timeline);

        var zone = planes.Where(p => p.Owner == "zone").ToList();
        var steady = zone.First(p => p.Opacity >= 0.95);
        foreach (var plane in zone.Where(p => p.Opacity >= 0.95))
        {
            Assert.That(plane.X, Is.EqualTo(steady.X).Within(1.5), $"zone plane x moved. {timeline}");
            Assert.That(plane.Y, Is.EqualTo(steady.Y).Within(1.5), $"zone plane y moved. {timeline}");
            Assert.That(plane.W, Is.EqualTo(steady.W).Within(1.0), $"zone plane width changed. {timeline}");
        }
        Assert.That(splashSize, Is.GreaterThan(0), "The splash size was not sampled.");
        Assert.That(steady.W, Is.EqualTo(splashSize).Within(1.0), "The component veil's plane is not the splash's size.");
    }
}
