// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework.Interfaces;

namespace Aetheus.E2E;

public abstract class E2ETestBase : BrowserTest
{
    private readonly List<string> _browserDiagnostics = [];
    private readonly List<Regex> _allowedBrowserDiagnostics = [];
    private IBrowserContext? _fixtureContext;
    private int _fixtureContextsOpened;
    private int _testsOnFixtureContext;

    // All harness configuration (URLs, admin creds, timeouts, readiness hook) lives in
    // PlaywrightConfig - each value env-overridable with the historical default. These
    // thin accessors keep the existing test call sites (FrontendUrl, AdminUser, …) intact.
    protected static string FrontendUrl => PlaywrightConfig.FrontendUrl;
    protected static string BackendUrl => PlaywrightConfig.BackendUrl;
    protected static string AdminUser => PlaywrightConfig.AdminUser;
    protected static string AdminEmail => PlaywrightConfig.AdminEmail;

    /// <summary>The seeded default organization (slug "aetheus"), read with an admin client. Since R-45
    /// (PLAN-003 P3-1) only Admin is held site-wide: a test user's other roles are held there.</summary>
    protected static async Task<int> DefaultOrganizationIdAsync(HttpClient admin)
    {
        var page = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonElement>(
            admin, $"{BackendUrl}/api/organizations?page=1&pageSize=100");
        return page.GetProperty("items").EnumerateArray()
            .Single(organization => organization.GetProperty("slug").GetString() == "aetheus")
            .GetProperty("id").GetInt32();
    }
    protected static string AdminPassword => PlaywrightConfig.AdminPassword;

    /// <summary>
    /// R-14 (owner decision 2026-10-28): <c>POST /api/users</c> takes no password; the account is closed until its
    /// welcome link is used. A test that signs in as a new account gives it <paramref name="password"/> over the
    /// API: the administrator's reset (which arms the forced change), then, unless
    /// <paramref name="mustChangePassword"/>, the user's own change, which clears it.
    /// </summary>
    protected static async Task CreateUserWithPasswordAsync(HttpClient admin, object createBody, string email, string password, bool mustChangePassword = false)
    {
        var create = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(admin, $"{BackendUrl}/api/users", createBody);
        create.EnsureSuccessStatusCode();
        var userId = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<JsonElement>(create.Content)).GetProperty("id").GetInt32();
        var adminSet = mustChangePassword ? password : password + "-first";
        (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(admin, $"{BackendUrl}/api/users/{userId}/change-password",
            new { NewPassword = adminSet })).EnsureSuccessStatusCode();
        if (mustChangePassword) return;

        using var user = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var login = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(user, $"{BackendUrl}/api/auth/login",
            new { Email = email, Password = adminSet });
        login.EnsureSuccessStatusCode();
        var token = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<JsonElement>(login.Content)).GetProperty("token").GetString();
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(user, $"{BackendUrl}/api/users/me/change-password",
            new { CurrentPassword = adminSet, NewPassword = password })).EnsureSuccessStatusCode();
    }

    private static int AppReadyTimeoutMs => PlaywrightConfig.AppReadyTimeoutMs;

    protected virtual bool UsePreauthenticatedContext => true;

    /// <summary>
    /// True for fixtures whose tests do not need a clean browser profile: they then share one
    /// authenticated context for the whole fixture and each test still gets a fresh page (its own
    /// routes, init scripts, viewport, sessionStorage and listeners). Fixtures that prove login,
    /// logout, permissions or a fresh profile keep the default, one context per test. A test that
    /// changes shared profile state inside a sharing fixture opts out with
    /// <see cref="IsolatedBrowserContextAttribute"/>. After a failed test the shared context is
    /// discarded so a broken state never reaches the next test.
    /// </summary>
    protected virtual bool ShareBrowserContextAcrossTests => false;

    /// <summary>The browser context of the current test (shared by the fixture or its own).</summary>
    public IBrowserContext Context { get; private set; } = null!;

    /// <summary>A fresh page opened for the current test.</summary>
    public IPage Page { get; private set; } = null!;

    public virtual BrowserNewContextOptions ContextOptions()
    {
        var options = new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            Locale = "en-US"
        };
        if (UsePreauthenticatedContext)
            options.StorageState = E2EAuthSession.StorageStateJson;
        return options;
    }

    // One SetUp method, so the page exists before the diagnostics hooks attach to it (NUnit does not
    // order two SetUp methods of the same class). BrowserTest's own SetUp has already provided Browser.
    [SetUp]
    public async Task OpenPageAndCaptureBrowserDiagnosticsAsync()
    {
        Context = await AcquireContextAsync();
        Page = await Context.NewPageAsync();

        _browserDiagnostics.Clear();
        _allowedBrowserDiagnostics.Clear();
        Page.Console += (_, message) =>
        {
            if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                var location = string.IsNullOrWhiteSpace(message.Location)
                    ? string.Empty
                    : $" [{message.Location}]";
                _browserDiagnostics.Add($"console error: {message.Text}{location}");
            }
        };
        Page.PageError += (_, exception) => _browserDiagnostics.Add($"page error: {exception}");
    }

    private bool UsesFixtureContext =>
        ShareBrowserContextAcrossTests && !IsolatedBrowserContextAttribute.AppliesToCurrentTest();

    private async Task<IBrowserContext> AcquireContextAsync()
    {
        // NewContext registers the context with BrowserTest, which closes it after the test.
        if (!UsesFixtureContext)
            return await NewContext(ContextOptions());

        // The worker replaces its browser after a failed test; a context of the old browser is dead.
        if (_fixtureContext is null || _fixtureContext.Browser != Browser || !Browser.IsConnected)
        {
            await CloseFixtureContextAsync();
            var context = await Browser.NewContextAsync(ContextOptions());
            context.Close += (_, closed) =>
            {
                if (ReferenceEquals(_fixtureContext, closed)) _fixtureContext = null;
            };
            _fixtureContext = context;
            _fixtureContextsOpened++;
        }

        _testsOnFixtureContext++;
        TestContext.Out.WriteLine(
            $"[E2E-CONTEXT] {GetType().Name}: test {_testsOnFixtureContext} on shared context #{_fixtureContextsOpened}.");
        return _fixtureContext;
    }

    private async Task CloseFixtureContextAsync()
    {
        var context = _fixtureContext;
        _fixtureContext = null;
        if (context is null) return;
        try
        {
            await context.CloseAsync();
        }
        catch (PlaywrightException)
        {
            // The browser that owned it is already gone.
        }
    }

    [OneTimeTearDown]
    public async Task CloseSharedBrowserContextAsync()
    {
        await CloseFixtureContextAsync();
    }

    /// <summary>
    /// Allows one explicit diagnostic pattern for a test that deliberately breaks a browser resource.
    /// The exception is scoped to that test; every other console/page error still fails the teardown.
    /// </summary>
    protected void AllowBrowserDiagnostic(Regex pattern) => _allowedBrowserDiagnostics.Add(pattern);

    protected async Task LoginAsync(bool force = false)
    {
        if (UsePreauthenticatedContext && !force)
            return;

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await Page.GotoAsync($"{FrontendUrl}/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                await Page.WaitForSelectorAsync("#Email", new() { Timeout = AppReadyTimeoutMs });

                await Page.FillAsync("#Email", AdminEmail);
                await Page.FillAsync("#Password", AdminPassword);
                await Page.ClickAsync("button[type='submit']");

                var submitStateHandle = await Page.WaitForFunctionAsync("""
                    () => {
                        if (location.pathname === '/') return 'root';
                        if (document.querySelector('.login-container .omni-alert--danger, .login-container .omni-alert'))
                            return 'login-error';
                        return null;
                    }
                    """, null, new() { Timeout = AppReadyTimeoutMs });
                var submitState = await submitStateHandle.JsonValueAsync<string>();
                if (submitState == "login-error")
                {
                    if (attempt == maxAttempts)
                        throw new InvalidOperationException($"Login failed after {maxAttempts} attempts.");
                    TestContext.Progress.WriteLine($"Login returned an authentication error; retrying attempt {attempt + 1}/{maxAttempts}.");
                    continue;
                }

                // The redirect to "/" fires before the authenticated shell is ready. A reset E2E
                // database can also invalidate a just-issued token while stale authenticated requests
                // finish draining; in that explicit case MainLayout sends us back to /login. Detect the
                // two terminal states instead of waiting 30 seconds for a panel that cannot appear, and
                // retry the complete login flow a bounded number of times.
                var stateHandle = await Page.WaitForFunctionAsync("""
                    () => {
                        const panel = document.querySelector('.omni-panel-menu');
                        if (panel && getComputedStyle(panel).visibility !== 'hidden') return 'ready';
                        if (location.pathname === '/login') return 'login';
                        return null;
                    }
                    """, null, new() { Timeout = AppReadyTimeoutMs });
                var state = await stateHandle.JsonValueAsync<string>();

                if (state == "ready")
                    return;

                if (attempt == maxAttempts)
                    throw new InvalidOperationException($"Login returned to /login after {maxAttempts} attempts.");
            }
            catch (PlaywrightException exception) when (
                attempt < maxAttempts &&
                exception.Message.Contains("ERR_NETWORK_CHANGED", StringComparison.Ordinal))
            {
                TestContext.Progress.WriteLine(
                    $"Login navigation hit ERR_NETWORK_CHANGED; retrying attempt {attempt + 1}/{maxAttempts}: {exception.Message}");
            }
        }
    }

    protected async Task NavigateToAsync(string path)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var diagnosticsBefore = _browserDiagnostics.Count;
            await Page.GotoAsync($"{FrontendUrl}/{path.TrimStart('/')}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            // A Windows network change (a Docker or Hyper-V adapter coming up) makes Chromium abort the
            // in-flight boot downloads with ERR_NETWORK_CHANGED, and index.html then shows its start
            // failure panel. That is the host, not the application: reload a bounded number of times,
            // and only when that exact error was logged during this attempt.
            var stateHandle = await Page.WaitForFunctionAsync(
                """
                readySelector => {
                    if (document.querySelector(readySelector)) return 'ready';
                    const failure = document.getElementById('app-start-error');
                    return failure && !failure.hidden ? 'start-failed' : null;
                }
                """,
                $"[data-testid='{PlaywrightConfig.BlazorReadyTestId}'], .omni-panel-menu, #Email",
                new() { Timeout = AppReadyTimeoutMs });
            var state = await stateHandle.JsonValueAsync<string>();
            var networkChanged = _browserDiagnostics
                .Skip(diagnosticsBefore)
                .Any(line => line.Contains("ERR_NETWORK_CHANGED", StringComparison.Ordinal));
            if (state == "start-failed" && networkChanged && attempt < maxAttempts)
            {
                TestContext.Progress.WriteLine(
                    $"Navigation to {path} hit ERR_NETWORK_CHANGED during boot; retrying attempt {attempt + 1}/{maxAttempts}.");
                _browserDiagnostics.RemoveRange(diagnosticsBefore, _browserDiagnostics.Count - diagnosticsBefore);
                continue;
            }

            await WaitForAppReadyAsync();
            return;
        }
    }

    /// <summary>
    /// Navigates the already loaded application to <paramref name="path"/> through the Blazor router,
    /// without a document reload: what a click on an internal link does. Use it only after a first
    /// <see cref="NavigateToAsync"/>, and keep a real reload wherever the reload itself is the proof.
    /// </summary>
    protected async Task NavigateInsideTheApplicationAsync(string path)
    {
        var target = new Uri(new Uri(FrontendUrl), path).ToString();
        var origin = await Page.EvaluateAsync<string>("() => location.href");
        // Blazor numbers its history entries: a route that redirects back to where it came from (an
        // edit route without permission returns to its detail page) leaves the address unchanged.
        var originEntry = await Page.EvaluateAsync<int?>("() => history.state?.['_index'] ?? null");
        if (string.Equals(origin, target, StringComparison.Ordinal))
        {
            await NavigateToAsync(path);
            return;
        }
        await Page.EvaluateAsync("""
            path => {
                if (!window.Blazor?.navigateTo) throw new Error('The Blazor router is not loaded on ' + location.href);
                window.Blazor.navigateTo(path);
            }
            """, path);
        // Read in the page, not through Playwright's URL tracking, which can miss a history change made
        // while the call above was returning. A new address or a new history entry proves the router ran.
        await Page.WaitForFunctionAsync(
            "([origin, entry]) => location.href !== origin || (history.state?.['_index'] ?? null) !== entry",
            new object?[] { origin, originEntry },
            new() { Timeout = AppReadyTimeoutMs });
        // The router renders the new route in the .NET call that follows the history change; two
        // animation frames later its first render batch, loaders included, is in the document.
        await Page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await WaitForAppReadyAsync();
    }

    // Replaces a brittle NetworkIdle wait: the app keeps a SignalR WebSocket
    // open, so the network is rarely "idle". Instead wait for the Blazor app to
    // become interactive - the authenticated shell renders the side nav, the
    // login page renders the username field.
    private async Task WaitForAppReadyAsync()
    {
        // Prefer the stable readiness hook on MainLayout's top-level container - it only
        // renders once the boot gate (auth/orgs/permissions) resolves, so it's a
        // deterministic "chrome is up" signal. The login page renders no chrome, so fall
        // back to the menu / username-field selector for that route.
        await Page.WaitForSelectorAsync(
            $"[data-testid='{PlaywrightConfig.BlazorReadyTestId}'], .omni-panel-menu, #Email",
            new() { State = WaitForSelectorState.Attached, Timeout = AppReadyTimeoutMs });

        // Wait for every page-level loading indicator to clear rather than sleeping a fixed delay.
        // Task-tracker loaders are intentionally non-centered and do not block page readiness.
        await WaitForNoSpinnerAsync();
    }

    protected async Task WaitForBlazorAsync()
    {
        await WaitForAppReadyAsync();
    }

    protected async Task WaitForDataGridAsync()
    {
        await Page.WaitForSelectorAsync(".omni-data-grid", new() { Timeout = AppReadyTimeoutMs });
    }

    protected async Task WaitForNoSpinnerAsync()
    {
        await Page.WaitForFunctionAsync("""
            () => [...document.querySelectorAll('.aetheus-loader')]
                .every(element => {
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display === 'none' || style.visibility === 'hidden' ||
                        rect.width === 0 || rect.height === 0;
                })
            """, null, new() { Timeout = AppReadyTimeoutMs });
    }

    // Match the menu item's exact text so "Logs" does not also match "System Logs".
    protected ILocator SidebarNavItem(string text)
    {
        return Page.Locator(".omni-panel-menu a.omni-panel-menu__link, .rz-panel-menu a.rz-navigation-item-link")
            .Filter(new()
            {
                Has = Page.GetByText(text, new() { Exact = true })
            });
    }

    protected async Task ClickSidebarNavItemAsync(string text)
    {
        var item = SidebarNavItem(text).First;
        await Expect(item).ToBeVisibleAsync(new() { Timeout = AppReadyTimeoutMs });
        await item.ClickAsync();
    }

    /// <summary>
    /// Cancels an E2E probe task, or proves that the agent completed it before cleanup won the race.
    /// The cancel endpoint deliberately returns 404 for terminal tasks, so treating every 404 as a
    /// missing task makes otherwise successful realtime tests depend on agent scheduling speed.
    /// </summary>
    protected async Task AssertTaskCancelledOrTerminalAsync(int taskId, string message)
    {
        var result = await Page.EvaluateAsync<JsonElement>(
            """
            async ({ backendUrl, taskId }) => {
                const token = localStorage.getItem('aetheus_auth_token');
                const headers = { 'Authorization': `Bearer ${token}` };
                const cancelResponse = await fetch(`${backendUrl}/api/tasks/${taskId}/cancel`, {
                    method: 'POST',
                    headers
                });

                if (cancelResponse.status === 200)
                    return { cancelStatus: 200, taskFetchStatus: null, taskStatus: null };

                const taskResponse = await fetch(`${backendUrl}/api/tasks/${taskId}`, { headers });
                const task = taskResponse.ok ? await taskResponse.json() : null;
                return {
                    cancelStatus: cancelResponse.status,
                    taskFetchStatus: taskResponse.status,
                    taskStatus: task?.status ?? null
                };
            }
            """,
            new { backendUrl = BackendUrl, taskId });

        var cancelStatus = result.GetProperty("cancelStatus").GetInt32();
        if (cancelStatus == 200)
            return;

        Assert.Multiple(() =>
        {
            Assert.That(cancelStatus, Is.EqualTo(404), message);
            Assert.That(result.GetProperty("taskFetchStatus").GetInt32(), Is.EqualTo(200),
                $"{message} The task must still exist when cancellation loses the terminal-state race.");
            Assert.That(result.GetProperty("taskStatus").GetString(),
                Is.AnyOf("Success", "Failed", "Timeout", "Cancelled"),
                $"{message} A rejected cancellation is valid only for an already terminal task.");
        });
    }

    /// <summary>
    /// Re-enrolls a seeded server through the real registration-token and public agent enrollment
    /// endpoints. Task-producing browser probes therefore exercise the production online-agent
    /// guard without a test-only database mutation or a weakened backend check.
    /// </summary>
    protected async Task ReenrollServerForTaskProbeAsync(int serverId)
    {
        var result = await Page.EvaluateAsync<JsonElement>(
            """
            async ({ backendUrl, serverId }) => {
                const token = localStorage.getItem('aetheus_auth_token');
                const authHeaders = {
                    'Authorization': `Bearer ${token}`,
                    'Content-Type': 'application/json'
                };
                const serverResponse = await fetch(`${backendUrl}/api/servers/${serverId}`, {
                    headers: { 'Authorization': `Bearer ${token}` }
                });
                const server = serverResponse.ok ? await serverResponse.json() : null;
                const tokenResponse = await fetch(`${backendUrl}/api/auth/registration-tokens`, {
                    method: 'POST',
                    headers: authHeaders,
                    body: JSON.stringify({ expirationHours: 1 })
                });
                const registration = tokenResponse.ok ? await tokenResponse.json() : null;
                const enrollmentResponse = server && registration
                    ? await fetch(`${backendUrl}/api/auth/register`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            registrationToken: registration.token,
                            hostname: server.hostname,
                            osDescription: server.osDescription || 'Linux E2E',
                            agentVersion: 'e2e-task-probe',
                            ipAddress: server.ipAddress || '127.0.0.1',
                            agentInstalledAt: new Date().toISOString(),
                            dockerAvailable: server.dockerAvailable ?? true,
                            pipelineRunnerAvailable: server.pipelineRunnerEnabled ?? true,
                            insecureTls: server.insecureTls ?? false
                        })
                    })
                    : null;
                return {
                    serverStatus: serverResponse.status,
                    tokenStatus: tokenResponse.status,
                    enrollmentStatus: enrollmentResponse?.status ?? 0
                };
            }
            """,
            new { backendUrl = BackendUrl, serverId });

        Assert.Multiple(() =>
        {
            Assert.That(result.GetProperty("serverStatus").GetInt32(), Is.EqualTo(200));
            Assert.That(result.GetProperty("tokenStatus").GetInt32(), Is.EqualTo(200));
            Assert.That(result.GetProperty("enrollmentStatus").GetInt32(), Is.EqualTo(200));
        });
    }

    // On failure, capture a screenshot plus the page URL/title so the harness
    // log shows *what* the page actually rendered (e.g. stuck on /login,
    // error boundary, blank app) instead of only an opaque timeout.
    [TearDown]
    public async Task CaptureOnFailureAsync()
    {
        var unexpectedDiagnostics = _browserDiagnostics
            .Where(diagnostic => !_allowedBrowserDiagnostics.Any(pattern => pattern.IsMatch(diagnostic)))
            .ToArray();
        var testAlreadyFailed = TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed;
        var failing = testAlreadyFailed || unexpectedDiagnostics.Length > 0;

        if (failing)
            await CaptureFailureEvidenceAsync();
        await ReleasePageAsync(discardFixtureContext: failing);

        if (!testAlreadyFailed && unexpectedDiagnostics.Length > 0)
        {
            Assert.Fail(
                "Unexpected browser errors were captured:\n  "
                + string.Join("\n  ", unexpectedDiagnostics));
        }
    }

    private async Task CaptureFailureEvidenceAsync()
    {
        try
        {
            var safeName = Regex.Replace(TestContext.CurrentContext.Test.Name, "[^A-Za-z0-9_]", "_");
            var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "e2e-failures");
            Directory.CreateDirectory(dir);
            var shot = Path.Combine(dir, $"{safeName}.png");
            await Page.ScreenshotAsync(new() { Path = shot, FullPage = true });
            var diagnostics = Path.Combine(dir, $"{safeName}.log");
            await File.WriteAllLinesAsync(diagnostics, _browserDiagnostics);
            TestContext.Out.WriteLine($"[E2E-DIAG] {safeName}: url='{Page.Url}' title='{await Page.TitleAsync()}' screenshot='{shot}' diagnostics='{diagnostics}'");
        }
        catch (PlaywrightException)
        {
            // Diagnostics are best-effort.
        }
    }

    // A per-test context is closed by BrowserTest; on a shared context the page itself must go, or
    // its SignalR connections and timers would keep running under the next test.
    private async Task ReleasePageAsync(bool discardFixtureContext)
    {
        if (!ReferenceEquals(Context, _fixtureContext))
            return;
        try
        {
            await Page.CloseAsync();
        }
        catch (PlaywrightException)
        {
            // The context or browser already closed it.
        }
        if (discardFixtureContext)
            await CloseFixtureContextAsync();
    }
}
