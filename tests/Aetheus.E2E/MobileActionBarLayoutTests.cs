// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// Site audit 2026-10-05, mobile findings measured at 390x844: the content area (not the document) scrolled
/// sideways, so the page-level mobile audit, which skips anything inside a scrolling parent, did not see them.
/// Each test measures the scrolling content area itself and the controls the audit found overlapping or cut.
/// </summary>
[Category("E2E")]
[Category("ResponsiveAdmin")]
[NonParallelizable]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class MobileActionBarLayoutTests : E2ETestBase
{
    private const int PhoneWidth = 390;
    private const int PhoneHeight = 844;

    // The content area that scrolls: a detail page's blade, otherwise the main area.
    private const string MeasureContent = """
        () => {
            const content = document.querySelector('.blade-content') ?? document.getElementById('main-content');
            if (!content) return ['missing'];
            const box = content.getBoundingClientRect();
            // Names the elements that stick out, so a failure says what to fix rather than only by how much.
            const culprits = () => [...content.querySelectorAll('*')]
                .filter(element => element.getBoundingClientRect().right > box.right + 1)
                .slice(-3)
                .map(element => `${element.tagName.toLowerCase()}.${[...element.classList].join('.')} right=${Math.round(element.getBoundingClientRect().right)}`);
            const overflow = content.scrollWidth > content.clientWidth + 1
                ? [`content ${content.scrollWidth}>${content.clientWidth} scrollLeft=${content.scrollLeft} (${culprits().join(', ')})`] : [];
            const outside = [...content.querySelectorAll('.omni-button, .omni-input')]
                .filter(element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    if (rect.width === 0 || style.visibility === 'hidden') return false;
                    for (let parent = element.parentElement; parent && parent !== content; parent = parent.parentElement) {
                        const scroll = getComputedStyle(parent).overflowX;
                        if (scroll === 'auto' || scroll === 'scroll' || scroll === 'hidden') return false;
                    }
                    return rect.left < box.left - 1 || rect.right > box.right + 1;
                })
                .map(element => `${element.tagName.toLowerCase()}[${element.getAttribute('aria-label') ?? element.textContent?.trim()}] outside`);
            // SITE-MOB-001/007: a button's content running past the button, or two buttons of one row overlapping.
            const overlaps = [...content.querySelectorAll('.omni-stack--horizontal')].flatMap(stack => {
                const buttons = [...stack.children].filter(child => child.classList.contains('omni-button'));
                const found = buttons.filter(button => button.scrollWidth > button.clientWidth + 1)
                    .map(button => `content of [${button.textContent?.trim()}] wider than its button`);
                for (let i = 0; i < buttons.length; i++)
                    for (let j = i + 1; j < buttons.length; j++) {
                        const a = buttons[i].getBoundingClientRect();
                        const b = buttons[j].getBoundingClientRect();
                        if (a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1)
                            found.push(`[${buttons[i].textContent?.trim()}] overlaps [${buttons[j].textContent?.trim()}]`);
                    }
                return found;
            });
            return [...overflow, ...outside, ...overlaps];
        }
        """;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    private async Task<string[]> MeasureAsync(string path)
    {
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight);
        await NavigateToAsync(path);
        await WaitForNoSpinnerAsync();
        return await Page.EvaluateAsync<string[]>(MeasureContent);
    }

    [Test]
    public async Task SiteMob002_NotificationToolbars_StayInsideTheContentArea()
    {
        var problems = await MeasureAsync("/admin/notifications");
        Assert.That(problems, Is.Empty, string.Join("; ", problems));
        var create = Page.GetByRole(AriaRole.Button, new() { Name = "Create" }).First;
        await Expect(create).ToBeInViewportAsync();
    }

    [Test]
    public async Task SiteMob003_LongApiPaths_BreakInsideTheirCards()
    {
        var problems = await MeasureAsync("/api-reference");
        var wrap = await Page.Locator(".api-ref-path").First.EvaluateAsync<string>("element => getComputedStyle(element).overflowWrap");
        Assert.Multiple(() =>
        {
            Assert.That(wrap, Is.EqualTo("anywhere"));
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        });
    }

    [Test]
    [Category("Servers")]
    public async Task SiteMob004_DockerGraphToolbar_WrapsAndResetViewKeepsTheGraphInPlace()
    {
        var problems = await MeasureAsync("/servers/1/docker?tab=relations");
        Assert.That(problems, Is.Empty, string.Join("; ", problems));

        var reset = Page.GetByRole(AriaRole.Button, new() { Name = "Reset view" });
        await reset.ClickAsync();
        await reset.ClickAsync();
        var after = await Page.EvaluateAsync<string[]>(MeasureContent);
        Assert.That(after, Is.Empty, "After Reset view: " + string.Join("; ", after));
    }

    [Test]
    [Category("Dashboard")]
    public async Task SiteMob001_DashboardWidgetActions_DoNotOverlap()
    {
        await NavigateToAsync("/dashboards");
        await WaitForNoSpinnerAsync();
        // The list opens a dashboard from its card's Edit button (no link), so follow it to learn the URL.
        var edit = Page.GetByRole(AriaRole.Button, new() { Name = "Edit" }).First;
        await Expect(edit).ToBeVisibleAsync();
        await edit.ClickAsync();
        await Page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/dashboards/\\d+"));
        var href = new Uri(Page.Url).AbsolutePath;

        var problems = await MeasureAsync(href);
        Assert.That(problems, Is.Empty, string.Join("; ", problems));
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Move Down" }).First).ToBeVisibleAsync();
    }

    [Test]
    [Category("Pipelines")]
    public async Task SiteMob005To008_VisualEditor_KeepsStagesNamesAndVariablesInsideAPhone()
    {
        // The audit's path: the editor opened on a desktop, then the window narrowed to a phone.
        await Page.SetViewportSizeAsync(1280, 900);
        await NavigateToAsync("pipelines");
        var pipeline = Page.Locator(".pipeline-name-link").Filter(new() { HasText = "toto-ci" }).First;
        await Expect(pipeline).ToBeVisibleAsync(new() { Timeout = 10000 });
        await pipeline.ClickAsync();
        var visualTab = Page.GetByRole(AriaRole.Tab).Filter(new() { Has = Page.GetByText("Visual", new() { Exact = true }) });
        await Expect(visualTab).ToBeVisibleAsync(new() { Timeout = 10000 });
        await visualTab.ClickAsync();
        await Expect(Page.Locator(".vp-node").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Page.GetByTitle("Fit to screen").ClickAsync();

        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight);
        await Page.Locator(".vp-toolbar .omni-button[title='Add Stage']").ClickAsync();
        var nodes = Page.Locator(".vp-node");
        await Expect(nodes).Not.ToHaveCountAsync(0);
        var last = nodes.Last;
        await last.Locator(".vp-node-header-title").ClickAsync();

        // SITE-MOB-005: no native scroll left in the frame, and the selected stage inside it.
        await Expect(last).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"vp-node-selected"));
        var frame = await Page.Locator(".vp-canvas-outer").EvaluateAsync<double[]>("element => { const r = element.getBoundingClientRect(); return [element.scrollLeft, element.scrollTop, r.left, r.right]; }");
        var selected = await last.EvaluateAsync<double[]>("element => { const r = element.getBoundingClientRect(); return [r.left, r.right]; }");

        // SITE-MOB-006: every stage name keeps a readable width.
        var titleWidths = await Page.Locator(".vp-node-header-title").EvaluateAllAsync<double[]>(
            "elements => elements.map(element => element.getBoundingClientRect().width)");

        // SITE-MOB-008: the variable grid and its add row, opened, then focus walked with Tab.
        await Page.EvaluateAsync("""
            () => document.querySelectorAll('details').forEach(details => {
                if (details.querySelector('.vp-settings-panel, .vp-var-grid')) details.open = true;
            })
            """);
        var valueBox = Page.Locator(".vp-settings-panel .omni-text-box[placeholder='Value']").First;
        await valueBox.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        var content = await Page.EvaluateAsync<double[]>("""
            () => { const c = document.getElementById('main-content'); return [c.scrollWidth, c.clientWidth, c.scrollLeft]; }
            """);

        Assert.Multiple(() =>
        {
            Assert.That(frame[0], Is.Zero, "The visual frame must not keep a native horizontal scroll.");
            Assert.That(frame[1], Is.Zero, "The visual frame must not keep a native vertical scroll.");
            Assert.That(selected[0], Is.GreaterThanOrEqualTo(frame[2] - 1), "The selected stage starts inside the frame.");
            Assert.That(selected[1], Is.LessThanOrEqualTo(frame[3] + 1), "The selected stage ends inside the frame.");
            Assert.That(titleWidths, Is.Not.Empty);
            Assert.That(titleWidths, Has.All.GreaterThanOrEqualTo(60), "Each stage name keeps at least 60px: " + string.Join(", ", titleWidths));
            Assert.That(content[0], Is.LessThanOrEqualTo(content[1] + 1), $"The content area must not be wider than the phone: {content[0]}>{content[1]}.");
            Assert.That(content[2], Is.Zero, "Tabbing through the variables must not scroll the content area sideways.");
        });
    }
}
