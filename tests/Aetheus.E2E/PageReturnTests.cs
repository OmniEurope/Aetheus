// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>Returning to the pipeline runs page restores content and scroll position immediately.
/// Reserve the full-page loader for an initial visit.</summary>
[Category("E2E")]
[Category("Pipelines")]
[RollbackRelevant]
public class PageReturnTests : E2ETestBase
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    /// <summary>The first block that scrolls: the page itself, else a scrolling block inside it.</summary>
    private const string ScrollerScript = """
        const scroller = () => {
            const main = document.getElementById('main-content');
            if (!main) return null;
            if (main.scrollHeight - main.clientHeight > 10) return main;
            return [...main.querySelectorAll('*')].find(element => {
                const overflow = getComputedStyle(element).overflowY;
                return (overflow === 'auto' || overflow === 'scroll') && element.scrollHeight - element.clientHeight > 10;
            }) ?? null;
        };
        """;

    [Test]
    public async Task GoingBackToTheRunsList_ShowsItAtOnceAtItsScrollPosition()
    {
        // A short window, so the runs are taller than the page and the scroll position means something.
        await Page.SetViewportSizeAsync(1280, 420);
        await NavigateToAsync("pipelines?tab=runs");
        var runLinks = Page.Locator("#main-content a[href*='/pipelines/runs/']");
        await Expect(runLinks.First).ToBeVisibleAsync(new() { Timeout = 15000 });

        // Scroll, then open a run that is in sight, without Playwright scrolling it into view.
        var scrolled = await Page.EvaluateAsync<int>("() => {" + ScrollerScript + """
                const element = scroller();
                if (!element) return -1;
                element.scrollTop = Math.min(80, element.scrollHeight - element.clientHeight);
                const box = element.getBoundingClientRect();
                const link = [...document.querySelectorAll("#main-content a[href*='/pipelines/runs/']")].find(candidate => {
                    const rect = candidate.getBoundingClientRect();
                    return rect.height > 0 && rect.top >= box.top && rect.bottom <= box.bottom;
                });
                if (!link) return -2;
                const top = element.scrollTop;
                link.click();
                return top;
            }
            """);
        Assert.That(scrolled, Is.GreaterThan(0), "The runs list must scroll and show a run in sight (-1: nothing scrolls, -2: no run in sight).");
        // A navigation inside the application: no load event, the address alone changes.
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/pipelines/runs/\d+"), new() { Timeout = 15000 });
        await WaitForNoSpinnerAsync();

        // Every frame from now on: did the list's page-wide loader ever show?
        await Page.EvaluateAsync("""
            () => {
                window.__r2087SawLoader = false;
                window.__r2087Frames = 0;
                const check = () => {
                    window.__r2087Frames++;
                    if (location.pathname === '/pipelines'
                        && document.querySelector('#main-content .aetheus-loader-centered')) window.__r2087SawLoader = true;
                    if (!window.__r2087Stop) requestAnimationFrame(check);
                };
                requestAnimationFrame(check);
            }
            """);
        // Going back restores the list from its state and refreshes it in the background: the window in
        // which a page-wide loader could show closes when that refresh has answered and rendered.
        var refreshAnswered = Task.WhenAll(
            Page.WaitForResponseAsync(response => response.Url.Contains("/api/pipelines/runs/recent", StringComparison.Ordinal)),
            Page.WaitForResponseAsync(response => response.Url.Contains("/api/pipelines/dependencies", StringComparison.Ordinal)));
        var started = DateTime.UtcNow;
        await Page.GoBackAsync(new() { WaitUntil = WaitUntilState.Commit });
        await Expect(runLinks.First).ToBeAttachedAsync(new() { Timeout = 15000 });
        var shownAfter = DateTime.UtcNow - started;
        await Page.WaitForFunctionAsync("expected => {" + ScrollerScript + """
                const element = scroller();
                return !!element && Math.abs(element.scrollTop - expected) <= 1;
            }
            """, scrolled, new() { Timeout = 5000 });
        await refreshAnswered;
        var framesAtRefresh = await Page.EvaluateAsync<int>("() => window.__r2087Frames");
        // The observer samples every frame: two more frames after the refresh answered cover its render.
        await Page.WaitForFunctionAsync("start => window.__r2087Frames >= start + 2", framesAtRefresh);
        var sawLoader = await Page.EvaluateAsync<bool>("() => { window.__r2087Stop = true; return window.__r2087SawLoader; }");

        TestContext.Progress.WriteLine($"R2-087: runs list shown {shownAfter.TotalMilliseconds:F0} ms after going back.");
        Assert.That(sawLoader, Is.False, "Going back showed the page-wide loader again.");
    }
}
