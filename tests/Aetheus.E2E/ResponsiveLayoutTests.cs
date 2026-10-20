// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>Guards the small-screen contract: content scrolls inside its component, never the page.</summary>
[Category("E2E")]
[Category("Dashboard")]
[NonParallelizable]
public sealed class ResponsiveLayoutTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    private static readonly string[] PrimaryMobilePaths =
    [
        "/",
        "/servers",
        "/projects",
        "/pipelines",
        "/releases",
        "/environments",
        "/tasks",
        "/alerts",
        "/backups",
        "/vaults",
        "/variable-libraries",
        "/service-connections",
        "/git-repositories",
        "/logs",
        "/settings"
    ];

    private static readonly string[] SecondaryMobilePaths =
    [
        "/servers/add-agent",
        "/servers/1",
        "/servers/1/overview",
        "/servers/1/configuration",
        "/servers/1/docker",
        "/servers/1/services",
        "/servers/1/tasks",
        "/servers/1/apps",
        "/servers/1/modules",
        "/servers/1/logs",
        "/servers/1/apache",
        "/servers/1/certbot",
        "/servers/1/cron",
        "/servers/1/firewall",
        "/servers/1/libraries",
        "/servers/1/mail",
        "/servers/1/pipelines",
        "/servers/1/portsentry",
        "/servers/1/projects",
        "/servers/1/properties",
        "/servers/1/releases",
        "/servers/1/rkhunter",
        "/servers/1/teamspeak",
        "/servers/1/updates",
        "/servers/1/vaults",
        "/projects/new",
        "/projects/3",
        "/projects/3/overview",
        "/projects/3/board",
        "/projects/3/edit",
        "/projects/3/artifacts",
        "/projects/3/environments",
        "/projects/3/external-repo",
        "/projects/3/libraries",
        "/projects/3/logs",
        "/projects/3/monitoring",
        "/projects/3/pipelines",
        "/projects/3/quality",
        "/projects/3/releases",
        "/projects/3/servers",
        "/projects/3/tasks",
        "/projects/3/vaults",
        "/pipelines/new",
        "/pipelines/1",
        "/pipelines/runs/1",
        "/environments/new",
        "/environments/1",
        "/vaults/new",
        "/vaults/1",
        "/variable-libraries/new",
        "/variable-libraries/1",
        "/releases/1",
        "/dashboards",
        "/admin",
        "/users",
        "/users/new",
        "/users/1",
        "/admin/roles",
        "/admin/roles/new",
        "/admin/roles/1",
        "/admin/organizations",
        "/admin/organizations/new",
        "/admin/organizations/1",
        "/admin/organizations/1/edit",
        "/admin/settings",
        "/admin/audit",
        "/admin/plugins",
        "/admin/notifications",
        "/admin/package-feeds",
        "/admin/system-logs",
        "/api-reference",
        "/help",
        "/help/dashboard"
    ];

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    [RollbackRelevant]
    public async Task PrimaryMobilePages_RemainReadableInsideTheViewport()
        => await AuditMobilePathsAsync(PrimaryMobilePaths, 375);

    [Test]
    [Category("Servers")]
    [RollbackRelevant]
    public async Task ServerMobilePages_RemainReadableInsideTheViewport()
        => await AuditMobilePathsAsync(SecondaryMobilePaths.Where(path => path.StartsWith("/servers/", StringComparison.Ordinal)), 375);

    [Test]
    public async Task MailMobileTabs_RemainReadableInsideTheViewport()
        => await AuditMobilePathsAsync([
            "/servers/1/mail?tab=spam",
            "/servers/1/mail?tab=queue",
            "/servers/1/mail?tab=diagnostics"
        ], 375);

    [Test]
    [RollbackRelevant]
    public async Task ProjectMobilePages_RemainReadableInsideTheViewport()
        => await AuditMobilePathsAsync(SecondaryMobilePaths.Where(path => path.StartsWith("/projects/", StringComparison.Ordinal)), 375);

    [Test]
    [Category("ResponsiveAdmin")]
    [RollbackRelevant]
    public async Task AdministrationMobilePages_RemainReadableInsideTheViewport()
        => await AuditMobilePathsAsync(SecondaryMobilePaths.Where(path =>
            path == "/admin" || path.StartsWith("/admin/", StringComparison.Ordinal) || path.StartsWith("/users", StringComparison.Ordinal)), 375);

    [Test]
    [RollbackRelevant]
    public async Task OtherSecondaryMobilePages_RemainReadableInsideTheViewport()
    {
        var artifactPath = await ResolveSeededArtifactPathAsync();
        var paths = SecondaryMobilePaths.Where(path =>
            !path.StartsWith("/servers/", StringComparison.Ordinal)
            && !path.StartsWith("/projects/", StringComparison.Ordinal)
            && path != "/admin"
            && !path.StartsWith("/admin/", StringComparison.Ordinal)
            && !path.StartsWith("/users", StringComparison.Ordinal))
            .Append(artifactPath);
        await AuditMobilePathsAsync(paths, 375);
    }

    private async Task<string> ResolveSeededArtifactPathAsync()
    {
        await NavigateToAsync("/projects/3/artifacts");
        await WaitForNoSpinnerAsync();
        var artifactLink = Page.Locator("a[href^='/artifacts/']").First;
        await Expect(artifactLink).ToBeVisibleAsync();
        return await artifactLink.GetAttributeAsync("href")
            ?? throw new InvalidOperationException("The seeded artifact link has no href.");
    }

    [Test]
    public async Task R503_PagesMadeOfTabs_DoNotScrollSidewaysOnADesktop()
    {
        // The tab row of OE ended 4px past its strip, and the content area scrolls sideways: every page
        // made of tabs had a scrollbar. Measured, not read: the content is no wider than what is shown.
        await Page.SetViewportSizeAsync(1280, 900);
        var failures = new List<string>();
        var pagesWithTabs = 0;
        foreach (var path in new[] { "/servers/1/services", "/servers/1/docker", "/servers/1/mail", "/projects/3/quality", "/projects/3/monitoring" })
        {
            await NavigateToAsync(path);
            await WaitForNoSpinnerAsync();
            var measure = await Page.EvaluateAsync<string>("""
                () => {
                    const content = document.querySelector('.blade-content');
                    if (!content) return 'missing';
                    const tabs = content.querySelector('.omni-tabs__viewport') ? 'tabs' : 'plain';
                    return content.scrollWidth > content.clientWidth
                        ? 'overflow:' + content.scrollWidth + '>' + content.clientWidth
                        : tabs;
                }
                """);
            if (measure == "tabs") pagesWithTabs++;
            else if (measure != "plain") failures.Add($"{path}: {measure}");
        }

        Assert.That(failures, Is.Empty, "Pages whose content area is wider than what it shows:\n" + string.Join("\n", failures));
        Assert.That(pagesWithTabs, Is.GreaterThan(0), "None of the pages measured had tabs: the test proved nothing.");
    }

    [Test]
    public async Task R2125_PipelineListsWithTheirStatusChips_DoNotScrollSidewaysOnADesktop()
    {
        // Measure the status-chip strip to detect overflow that could create a second content
        // scrollbar.
        await Page.SetViewportSizeAsync(1014, 900);
        var failures = new List<string>();
        var pagesWithChips = 0;
        foreach (var path in new[] { "/pipelines", "/projects/3/pipelines" })
        {
            await NavigateToAsync(path);
            await WaitForNoSpinnerAsync();
            var measure = await Page.EvaluateAsync<string>("""
                () => {
                    // A project's pages scroll inside its blade, the top-level list inside the main area.
                    const content = document.querySelector('.blade-content') ?? document.getElementById('main-content');
                    if (!content) return 'missing';
                    const chips = content.querySelector('.omni-stack-scroll > .omni-stack--overflow-scroll') ? 'chips' : 'plain';
                    return content.scrollWidth > content.clientWidth
                        ? 'overflow:' + content.scrollWidth + '>' + content.clientWidth
                        : chips;
                }
                """);
            if (measure == "chips") pagesWithChips++;
            else if (measure != "plain") failures.Add($"{path}: {measure}");
        }

        Assert.That(failures, Is.Empty, "Pages whose content area is wider than what it shows:\n" + string.Join("\n", failures));
        Assert.That(pagesWithChips, Is.GreaterThan(0), "None of the pages measured had a chip strip: the test proved nothing.");
    }

    [Test]
    public async Task RepresentativePages_RemainReadableAcrossCommonWidths()
    {
        var representativePaths = new[] { "/", "/servers", "/projects", "/pipelines", "/settings" };
        foreach (var viewportWidth in new[] { 390, 430 })
            await AuditMobilePathsAsync(representativePaths, viewportWidth);
    }

    [Test]
    public async Task TabletBand_UsesCompactOverlayAtEveryBoundaryWidth()
    {
        var representativePaths = new[] { "/", "/admin/audit", "/pipelines/1" };
        foreach (var viewportWidth in new[] { 767, 769, 800, 1023 })
            await AuditMobilePathsAsync(representativePaths, viewportWidth);
    }

    [Test]
    public async Task AboveTabletBreakpoint_RestoresDesktopRail()
    {
        await Page.SetViewportSizeAsync(1024, 812);
        await NavigateToAsync("/");
        await WaitForNoSpinnerAsync();

        var sidebar = Page.Locator(".omni-sidebar");
        await Expect(sidebar).ToBeVisibleAsync();
        await Expect(sidebar).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("omni-sidebar--closed"));
        var width = await sidebar.EvaluateAsync<double>("element => element.getBoundingClientRect().width");
        Assert.That(width, Is.GreaterThan(200));
        await Expect(Page.Locator(".omni-sidebar__backdrop")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task MobileColdLoad_NeverPaintsAnOpenDrawerBeforeViewportHandshake()
    {
        await Page.SetViewportSizeAsync(390, 812);
        await NavigateToAsync("/");
        await Page.AddInitScriptAsync("""
            window.__aetheusColdLoadViolations = [];
            requestAnimationFrame(function sampleColdLoadFrame() {
                const backdrop = document.querySelector('.omni-sidebar__backdrop');
                if (backdrop) {
                    const style = getComputedStyle(backdrop);
                    const rect = backdrop.getBoundingClientRect();
                    if (style.display !== 'none' && style.visibility !== 'hidden'
                        && rect.width > 0 && rect.height > 0) {
                        window.__aetheusColdLoadViolations.push('visible backdrop');
                    }
                }

                const sidebar = document.querySelector('.omni-sidebar');
                if (sidebar) {
                    const style = getComputedStyle(sidebar);
                    const rect = sidebar.getBoundingClientRect();
                    if (!sidebar.classList.contains('omni-sidebar--closed')
                        && style.visibility !== 'hidden' && rect.width > 1) {
                        window.__aetheusColdLoadViolations.push('open drawer');
                    }
                }

                if (!document.querySelector('[data-viewport="ready"]')) {
                    requestAnimationFrame(sampleColdLoadFrame);
                }
            });
            """);

        await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.WaitForSelectorAsync("[data-viewport='ready']", new()
        {
            State = WaitForSelectorState.Attached,
            Timeout = PlaywrightConfig.AppReadyTimeoutMs
        });
        var violations = await Page.EvaluateAsync<string[]>(
            "() => window.__aetheusColdLoadViolations ?? []");

        Assert.That(violations, Is.Empty,
            "The splash/viewport handshake must prevent an open mobile drawer or backdrop from painting.");
    }

    [Test]
    public async Task MobileBackdrop_ClosesDrawerFromARealPointerClick()
    {
        await Page.SetViewportSizeAsync(390, 812);
        await NavigateToAsync("/");
        await Page.Locator("[aria-label='Toggle sidebar']").ClickAsync();
        await Expect(Page.Locator(".omni-sidebar__backdrop")).ToBeVisibleAsync();

        const int exposedBackdropX = 350;
        const int exposedBackdropY = 400;
        var targetClass = await Page.EvaluateAsync<string>(
            "() => document.elementFromPoint(350, 400)?.getAttribute('class') ?? ''");
        Assert.That(targetClass, Does.Contain("omni-sidebar__backdrop"));

        await Page.Mouse.ClickAsync(exposedBackdropX, exposedBackdropY);

        await Expect(Page.Locator(".omni-sidebar__backdrop")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task MobileCurrentRouteLink_ClosesDrawer()
    {
        await Page.SetViewportSizeAsync(390, 812);
        await NavigateToAsync("/");
        await Page.Locator("[aria-label='Toggle sidebar']").ClickAsync();
        await Expect(Page.Locator(".omni-sidebar__backdrop")).ToBeVisibleAsync();
        await Page.WaitForFunctionAsync("""
            () => document.querySelector('.omni-sidebar__panel')?.getBoundingClientRect().width >= 240
            """);

        await SidebarNavItem("Dashboard").ClickAsync();

        await Expect(Page.Locator(".omni-sidebar__backdrop")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task RepresentativePages_RemainReadableInPhoneLandscape()
        => await AuditMobilePathsAsync(["/", "/servers", "/projects"], 812, 390);

    [Test]
    [IsolatedBrowserContext]
    public async Task UnauthenticatedLogin_RemainsReadableInPortraitAndLandscape()
    {
        await NavigateToAsync("/");
        await Page.EvaluateAsync("() => localStorage.clear()");

        foreach (var viewport in new[] { (Width: 390, Height: 812), (Width: 812, Height: 390) })
        {
            await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            await Page.GotoAsync($"{FrontendUrl}/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(Page.Locator("#Username")).ToBeVisibleAsync();
            var widths = await Page.EvaluateAsync<int[]>(
                "() => [document.documentElement.scrollWidth, window.innerWidth]");
            Assert.That(widths[0], Is.LessThanOrEqualTo(widths[1]),
                $"The unauthenticated login must fit {viewport.Width}x{viewport.Height}.");
        }
    }

    private async Task AuditMobilePathsAsync(IEnumerable<string> paths, int viewportWidth, int viewportHeight = 812)
    {
        var failures = new List<string>();
        var pendingApiRequests = new HashSet<IRequest>();
        var networkFailures = new List<string>();
        var networkGate = new object();
        void TrackRequest(object? _, IRequest request)
        {
            if (!request.Url.StartsWith(BackendUrl, StringComparison.OrdinalIgnoreCase)) return;
            lock (networkGate) pendingApiRequests.Add(request);
        }
        void FinishRequest(object? _, IRequest request)
        {
            lock (networkGate) pendingApiRequests.Remove(request);
        }
        void FailRequest(object? _, IRequest request)
        {
            lock (networkGate)
            {
                pendingApiRequests.Remove(request);
                networkFailures.Add($"{request.Method} {request.Url}: {request.Failure}");
            }
        }
        var serverErrors = new List<string>();
        void TrackResponse(object? _, IResponse response)
        {
            if (response.Status < 400 || !response.Url.StartsWith(BackendUrl, StringComparison.OrdinalIgnoreCase)) return;
            var entry = $"HTTP {response.Status} {response.Request.Method} {response.Url}";
            lock (networkGate)
            {
                networkFailures.Add(entry);
                if (response.Status >= 500) serverErrors.Add(entry);
            }
        }

        Page.Request += TrackRequest;
        Page.RequestFinished += FinishRequest;
        Page.RequestFailed += FailRequest;
        Page.Response += TrackResponse;
        try
        {
            // The first route of a sweep is a full load at this viewport, which proves the cold start at
            // that width; the next routes navigate inside the application, as a user does.
            var fullLoad = true;
            foreach (var path in paths)
            {
                try
                {
                    await AuditMobilePathAsync(path, viewportWidth, viewportHeight, fullLoad);
                    fullLoad = false;

                    // A page can still render its layout while one of its API calls failed on the
                    // server, and every layout check above then passes: a 5xx fails the route on its own.
                    string[] routeServerErrors;
                    lock (networkGate)
                    {
                        routeServerErrors = [.. serverErrors];
                        serverErrors.Clear();
                        networkFailures.Clear();
                    }
                    if (routeServerErrors.Length > 0)
                    {
                        failures.Add($"{viewportWidth}x{viewportHeight} {path}: the page rendered but its API answered a server error."
                            + $" Network: {string.Join("; ", routeServerErrors)}");
                    }
                }
                catch (Exception exception) when (exception is AssertionException or MultipleAssertException or PlaywrightException or TimeoutException)
                {
                    string networkState;
                    lock (networkGate)
                    {
                        var pending = pendingApiRequests.Select(request => $"pending {request.Method} {request.Url}");
                        networkState = string.Join("; ", networkFailures.Concat(pending));
                        networkFailures.Clear();
                        serverErrors.Clear();
                    }
                    failures.Add($"{viewportWidth}x{viewportHeight} {path}: {exception.Message}"
                        + (string.IsNullOrEmpty(networkState) ? string.Empty : $" Network: {networkState}"));
                    var screenshotDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "mobile-audit-failures");
                    Directory.CreateDirectory(screenshotDirectory);
                    var safeName = path == "/" ? "dashboard" : string.Concat(path.Trim('/').Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
                    await Page.ScreenshotAsync(new()
                    {
                        Path = Path.Combine(screenshotDirectory, $"{viewportWidth}x{viewportHeight}-{safeName}.png"),
                        FullPage = false
                    });

                    try
                    {
                        // A failed route can leave the browser on an error or login page. Restore a known
                        // authenticated desktop state so one defect does not cascade into every later route.
                        await Page.SetViewportSizeAsync(1280, 720);
                        // The context is already signed in, and /login sends a signed-in user away: sign in again
                        // only when the session was really lost.
                        await NavigateToAsync("");
                        if (await Page.Locator("#Username").CountAsync() > 0)
                            await LoginAsync(force: true);
                    }
                    catch (Exception recoveryException) when (
                        recoveryException is InvalidOperationException or PlaywrightException or TimeoutException)
                    {
                        failures.Add($"{viewportWidth}px recovery after {path}: {recoveryException.Message}");
                        break;
                    }
                    // The recovery left the login flow: the next route starts again from a full load.
                    fullLoad = true;
                }
            }
        }
        finally
        {
            Page.Request -= TrackRequest;
            Page.RequestFinished -= FinishRequest;
            Page.RequestFailed -= FailRequest;
            Page.Response -= TrackResponse;
        }

        Assert.That(failures, Is.Empty, () => string.Join(Environment.NewLine, failures));
    }

    private async Task AuditMobilePathAsync(string path, int viewportWidth, int viewportHeight, bool fullLoad)
    {
        await Page.SetViewportSizeAsync(viewportWidth, viewportHeight);
        if (fullLoad)
            await NavigateToAsync(path);
        else
            await NavigateInsideTheApplicationAsync(path);
        await WaitForNoSpinnerAsync();
        await Page.WaitForSelectorAsync(".omni-sidebar__backdrop", new()
        {
            State = WaitForSelectorState.Detached,
            Timeout = 5000
        });
        await Page.WaitForFunctionAsync("""
            () => (document.querySelector('.omni-sidebar')?.getBoundingClientRect().width ?? 0) <= 1
            """, null, new() { Timeout = 5000 });
        await Page.WaitForFunctionAsync("""
            () => [...document.querySelectorAll(".omni-data-grid[aria-busy='true']")]
                .every(element => {
                    const style = getComputedStyle(element);
                    return style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0';
                })
            """, null, new() { Timeout = 30000 });

        var collapsedSidebarState = await Page.Locator(".omni-sidebar").EvaluateAsync<string[]>("""
            element => {
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return [
                    element.className,
                    `${rect.width}`,
                    `${element.hidden}`,
                    element.getAttribute('aria-hidden') ?? '',
                    `${element.inert}`
                ];
            }
            """);

        Assert.Multiple(() =>
        {
            Assert.That(collapsedSidebarState[0], Does.Contain("omni-sidebar--closed"),
                $"{path} must collapse the mobile drawer after a viewport change.");
            Assert.That(double.Parse(collapsedSidebarState[1], System.Globalization.CultureInfo.InvariantCulture),
                Is.LessThanOrEqualTo(1), $"{path} must leave no visible drawer width.");
            Assert.That(collapsedSidebarState[2], Is.EqualTo("true"),
                $"{path} must remove the collapsed drawer from layout.");
            Assert.That(collapsedSidebarState[3], Is.EqualTo("true"),
                $"{path} must hide the collapsed drawer from assistive technology.");
            Assert.That(collapsedSidebarState[4], Is.EqualTo("true"),
                $"{path} must make the collapsed drawer inert.");
        });

        var widths = await Page.EvaluateAsync<int[]>("""
            () => [document.body.scrollWidth, window.innerWidth]
            """);
        var overflowingElements = await Page.EvaluateAsync<string[]>("""
            () => [...document.querySelectorAll('body *')]
                .filter(element => {
                    const style = getComputedStyle(element);
                    if (style.display === 'none' || style.visibility === 'hidden') return false;
                    if (element.classList.contains('monaco-aria-container')) return false;
                    const rect = element.getBoundingClientRect();
                    if (rect.width <= 0 || (rect.right <= window.innerWidth + 1 && rect.left >= -1)) return false;
                    for (let parent = element.parentElement; parent; parent = parent.parentElement) {
                        const overflow = getComputedStyle(parent).overflowX;
                        if (overflow === 'auto' || overflow === 'scroll' || overflow === 'hidden') return false;
                    }
                    return true;
                })
                .slice(0, 12)
                .map(element => {
                    const rect = element.getBoundingClientRect();
                    const name = element.id ? `#${element.id}` : `.${[...element.classList].join('.')}`;
                    return `${element.tagName.toLowerCase()}${name} [${Math.round(rect.left)}, ${Math.round(rect.right)}]`;
                })
            """);
        Assert.That(widths[0], Is.LessThanOrEqualTo(widths[1]),
            $"{path} must keep horizontal scrolling inside tabs, badges or data grids. Overflow: {string.Join("; ", overflowingElements)}");
        Assert.That(overflowingElements, Is.Empty,
            $"{path} must not expose visible content outside the viewport.");

        var clippedInteractiveElements = await Page.EvaluateAsync<string[]>("""
            () => [...document.querySelectorAll('a, button, input, select, textarea, [role="button"]')]
                .filter(element => {
                    const style = getComputedStyle(element);
                    if (style.display === 'none' || style.visibility === 'hidden') return false;
                    const rect = element.getBoundingClientRect();
                    if (rect.width === 0 || rect.height === 0) return false;
                    if (rect.left >= 0 && rect.right <= window.innerWidth) return false;
                    for (let parent = element.parentElement; parent; parent = parent.parentElement) {
                        const overflow = getComputedStyle(parent).overflowX;
                        if (overflow === 'auto' || overflow === 'scroll') return false;
                    }
                    return true;
                })
                .map(element => `${element.tagName.toLowerCase()}[${element.getAttribute('aria-label') ?? element.textContent?.trim() ?? ''}]`)
            """);

        Assert.That(clippedInteractiveElements, Is.Empty,
            $"{path} must not place interactive controls outside the mobile viewport.");

        var obscuredHeaderControls = await Page.EvaluateAsync<string[]>("""
            () => {
                const modalIsVisible = [...document.querySelectorAll('.omni-overlay')]
                    .some(element => {
                        const style = getComputedStyle(element);
                        const rect = element.getBoundingClientRect();
                        return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                    });
                if (modalIsVisible) return [];

                return ['.app-home-link', '.omni-app-menu__trigger'].flatMap(selector => {
                    // The drawer header repeats the logo link; it is hidden while the drawer is closed.
                    const elements = [...document.querySelectorAll(selector)].filter(element => !element.closest('.sidebar-drawer-header'));
                    return elements.length === 0 ? [{ missingSelector: selector }] : elements;
                })
                .filter(element => {
                    if (element.missingSelector) return true;
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    if (style.display === 'none' || style.visibility === 'hidden' || rect.width === 0 || rect.height === 0)
                        return true;
                    const top = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
                    return top === null || !(element === top || element.contains(top));
                })
                .map(element => {
                    if (element.missingSelector) return `${element.missingSelector} missing`;
                    const rect = element.getBoundingClientRect();
                    const top = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
                    return `${element.className} covered by ${top?.className ?? top?.tagName ?? 'nothing'}`;
                });
            }
            """);

        Assert.That(obscuredHeaderControls, Is.Empty,
            $"{path} must keep the home and user controls visible above page content.");

        var clippedEmptyStateText = await Page.EvaluateAsync<string[]>("""
            () => [...document.querySelectorAll('.empty-state')]
                .flatMap(state => [...state.children])
                .filter(element => {
                    if (element.classList.contains('empty-state-icon') ||
                        element.classList.contains('empty-state-actions') ||
                        !element.textContent?.trim()) return false;
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' &&
                        (rect.left < 0 || rect.right > window.innerWidth || element.scrollWidth > element.clientWidth + 1);
                })
                .map(element => element.textContent?.trim() ?? '')
            """);

        Assert.That(clippedEmptyStateText, Is.Empty,
            $"{path} must keep empty-state titles and descriptions readable inside the viewport.");

        var errorCardCount = await Page.Locator(".error-card").CountAsync();
        var errorCardText = string.Join(" | ", await Page.Locator(".error-card").AllInnerTextsAsync());
        var notFoundCount = await Page.Locator(".not-found-container").CountAsync();
        Assert.Multiple(() =>
        {
            Assert.That(errorCardCount, Is.Zero,
                $"{path} must not render the application error boundary. Card: {errorCardText}");
            Assert.That(notFoundCount, Is.Zero,
                $"{path} must resolve to a real page in the demo dataset.");
        });

    }

    [Test]
    public async Task MobileSettingsTabs_ScrollInsideTheTabStrip()
    {
        await NavigateToAsync("/admin/settings");
        await Page.SetViewportSizeAsync(375, 812);
        var tabs = Page.Locator(".url-synced-tabs .omni-tabs__viewport");
        await Expect(tabs).ToBeVisibleAsync();

        var overflowX = await tabs.EvaluateAsync<string>("element => getComputedStyle(element).overflowX");
        Assert.That(overflowX, Is.EqualTo("auto"));
    }

    [Test]
    [Category("Pipelines")]
    public async Task MobileRunLogs_ScrollInsideTheTerminal()
    {
        // Run 1 of the demo dataset, served with one finished step whose task has 400 log lines, so
        // the terminal always has more content than the phone can show. Everything else stays real.
        const int taskId = 987654;
        await Page.RouteAsync("**/api/pipelines/runs/1", async route =>
        {
            var response = await route.FetchAsync();
            var run = System.Text.Json.Nodes.JsonNode.Parse(await response.TextAsync())!.AsObject();
            var finishedAt = DateTime.UtcNow;
            run["status"] = "Success";
            run["completedAt"] = finishedAt;
            run["steps"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = taskId,
                ["stageName"] = "build",
                ["stepName"] = "long-log",
                ["status"] = "Success",
                ["taskId"] = taskId,
                ["startedAt"] = finishedAt.AddMinutes(-1),
                ["completedAt"] = finishedAt
            });
            await route.FulfillAsync(new() { Response = response, Body = run.ToJsonString() });
        });
        // Match the log route independently of its query because step-tail requests include
        // maxLines.
        await Page.RouteAsync(url => new Uri(url).AbsolutePath.EndsWith($"/api/logs/task/{taskId}", StringComparison.Ordinal), route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, 400).Select(line => new
            {
                id = line,
                taskId,
                level = "Info",
                message = $"log line {line}",
                timestamp = DateTime.UtcNow
            }))
        }));

        await Page.SetViewportSizeAsync(375, 812);
        await NavigateToAsync("/pipelines/runs/1?tab=logs");

        var terminal = Page.Locator(".run-split-logs .run-logs-terminal");
        await Expect(terminal.Locator(".run-log-line").First).ToBeVisibleAsync(new() { Timeout = 15000 });
        await terminal.ScrollIntoViewIfNeededAsync();

        var size = await terminal.EvaluateAsync<double[]>("element => [element.scrollHeight, element.clientHeight]");
        Assert.That(size[1], Is.GreaterThan(0), "The terminal must have a visible height on a phone.");
        Assert.That(size[0], Is.GreaterThan(size[1] + 1), "The terminal must hold more lines than it shows.");

        // The step opens at its tail (auto-follow): scroll up, then check the terminal itself moved.
        var before = await terminal.EvaluateAsync<double>("element => element.scrollTop");
        var box = await terminal.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null);
        await Page.Mouse.MoveAsync(box!.X + (box.Width / 2), box.Y + (box.Height / 2));
        await Page.Mouse.WheelAsync(0, before > 0 ? -600 : 600);
        await Page.WaitForFunctionAsync(
            "([selector, start]) => Math.abs(document.querySelector(selector).scrollTop - start) > 50",
            new object[] { ".run-split-logs .run-logs-terminal", before });
    }

    [Test]
    [Category("Pipelines")]
    public async Task R2095_MobileApprovalBanner_KeepsEverythingInside_AndReducesApproveAndRejectToIcons()
    {
        // Serve a waiting approval with a long stage name to test banner containment on mobile;
        // other responses remain real.
        await Page.RouteAsync("**/api/pipelines/runs/1", async route =>
        {
            var response = await route.FetchAsync();
            var run = System.Text.Json.Nodes.JsonNode.Parse(await response.TextAsync())!.AsObject();
            run["status"] = "WaitingForApproval";
            run["completedAt"] = null;
            await route.FulfillAsync(new() { Response = response, Body = run.ToJsonString() });
        });
        await Page.RouteAsync("**/api/pipelines/runs/1/approvals", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 424242,
                    pipelineRunId = 1,
                    stageName = "Confirm-the-production-switch-before-the-blue-green-swap",
                    scope = "Pipeline",
                    status = "Pending",
                    requestedAt = DateTime.UtcNow
                }
            })
        }));

        await Page.SetViewportSizeAsync(375, 812);
        await NavigateToAsync("/pipelines/runs/1");

        var banner = Page.Locator(".run-approval-banner");
        await Expect(banner.Locator(".approval-decision").First).ToBeVisibleAsync(new() { Timeout = 15000 });

        var overflowing = await banner.EvaluateAsync<string[]>("""
            banner => {
                const box = banner.getBoundingClientRect();
                return [...banner.querySelectorAll('*')]
                    .filter(element => {
                        const rect = element.getBoundingClientRect();
                        return rect.width > 0 && (rect.left < box.left - 1 || rect.right > box.right + 1);
                    })
                    .map(element => `${element.tagName}.${element.className}`);
            }
            """);
        var widths = await Page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, window.innerWidth]");
        var decisions = await banner.Locator(".approval-decision").EvaluateAllAsync<string[]>("""
            buttons => buttons.map(button => {
                const label = button.querySelector('.omni-button__content > span:not(.omni-icon)');
                return `${button.getAttribute('aria-label')}|${button.getAttribute('title')}|${label ? getComputedStyle(label).display : 'none'}`;
            })
            """);
        var commentLabelDisplay = await banner.Locator(".approval-add-comment .omni-button__content > span:not(.omni-icon)")
            .EvaluateAsync<string>("label => getComputedStyle(label).display");

        Assert.Multiple(() =>
        {
            Assert.That(overflowing, Is.Empty, "Nothing may run past the approval banner on a phone.");
            Assert.That(widths[0], Is.LessThanOrEqualTo(widths[1]), "The run page must not scroll sideways on a phone.");
            Assert.That(decisions, Has.Length.EqualTo(2));
            Assert.That(decisions, Has.All.Matches<string>(decision =>
                decision is not null && decision.EndsWith("|none", StringComparison.Ordinal) && !decision.StartsWith('|') && decision.Split('|')[0] == decision.Split('|')[1]),
                "Approve and Reject show their icon alone, keeping their accessible name and tooltip: " + string.Join(", ", decisions));
            Assert.That(commentLabelDisplay, Is.Not.EqualTo("none"), "Comment keeps its text.");
        });
    }

    [Test]
    [Category("Projects")]
    public async Task MobileProjectSettings_UseTheAvailableWidth()
    {
        await NavigateToAsync("/projects/3/edit");
        await Page.SetViewportSizeAsync(375, 812);

        var settings = Page.Locator(".project-ci-settings-row .omni-form-field");
        await Expect(settings).ToHaveCountAsync(3);
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.project-ci-settings-row .omni-form-field')?.getBoundingClientRect().width >= 280");
        var settingWidths = await settings.EvaluateAllAsync<double[]>(
            "elements => elements.map(element => element.getBoundingClientRect().width)");
        Assert.That(settingWidths, Has.Length.EqualTo(3));
        Assert.That(settingWidths, Has.All.GreaterThanOrEqualTo(280));
    }
}
