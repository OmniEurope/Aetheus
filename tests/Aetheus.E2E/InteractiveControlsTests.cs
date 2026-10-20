// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("Security")]
public sealed class InteractiveControlsTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    public async Task OwnerType_RadioTextClick_RevealsAndHidesProjectSelector()
    {
        await NavigateToAsync("vaults/new");

        var projectText = Page.GetByText("Project", new() { Exact = true }).First;
        await Expect(projectText).ToBeVisibleAsync(new() { Timeout = 10000 });
        await projectText.ClickAsync();

        var projectRadio = Page.GetByRole(AriaRole.Radio, new() { Name = "Project", Exact = true });
        await Expect(projectRadio).ToBeCheckedAsync(new() { Timeout = 5000 });
        var projectSelector = Page.Locator("select.omni-drop-down");
        await Expect(projectSelector).ToBeVisibleAsync(new() { Timeout = 5000 });

        // Clicks the visible radio label to exercise the user's interaction path.
        var globalText = Page.GetByText("Global", new() { Exact = true });
        await Expect(globalText).ToBeVisibleAsync();
        await globalText.ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Radio, new() { Name = "Global", Exact = true }))
            .ToBeCheckedAsync(new() { Timeout = 5000 });
        await Expect(projectSelector).ToHaveCountAsync(0, new() { Timeout = 5000 });
    }

    [Test]
    public async Task PasswordRuleFeedback_UpdatesOnInputWithoutBlur()
    {
        await NavigateToAsync("users");
        var adminLink = Page.Locator("a.server-name-link")
            .Filter(new() { HasText = AdminUser })
            .First;
        await Expect(adminLink).ToBeVisibleAsync(new() { Timeout = 10000 });
        await adminLink.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"/users/\d+$"), new() { Timeout = 10000 });

        var securityTab = Page.GetByRole(AriaRole.Tab)
            .Filter(new() { Has = Page.GetByText("Security", new() { Exact = true }) });
        await Expect(securityTab).ToBeVisibleAsync(new() { Timeout = 10000 });
        await securityTab.ClickAsync();

        var password = Page.Locator("input[type='password']").First;
        var feedback = Page.GetByText("Password must be at least 12 characters.", new() { Exact = true });
        await Expect(password).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(feedback).ToHaveClassAsync(new Regex("omni-u-text-muted"));

        await password.FillAsync("123456789012");

        await Expect(password).ToBeFocusedAsync();
        await Expect(feedback).ToHaveClassAsync(new Regex("omni-u-text-success"), new() { Timeout = 5000 });
    }

    [Test]
    public async Task SignalRDrop_WithHealthyBackend_StaysUsableAndReconnectsAutomatically()
    {
        await NavigateToAsync("servers");
        var webServer = Page.Locator("a.server-name-link").Filter(new() { HasText = "web-01" }).First;
        var serverHref = await webServer.GetAttributeAsync("href");
        var serverMatch = Regex.Match(serverHref ?? string.Empty, @"/servers/(\d+)/");
        Assert.That(serverMatch.Success, Is.True, "The seeded web-01 server must expose its id.");
        var serverId = int.Parse(serverMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        await ReenrollServerForTaskProbeAsync(serverId);

        AllowBrowserDiagnostic(new Regex(
            @"ERR_FAILED|Failed to (?:start|complete)|WebSocket.*failed|Failed to fetch",
            RegexOptions.IgnoreCase));
        var dialog = Page.Locator(".omni-connection-overlay");
        await Expect(dialog).ToHaveCountAsync(0);

        const string taskHubPattern = "**/hubs/servers**";
        var blockedHubRequests = 0;
        await Page.RouteAsync(taskHubPattern, route =>
        {
            Interlocked.Increment(ref blockedHubRequests);
            return route.AbortAsync();
        });
        try
        {
            // Reloading keeps the independently served WASM frontend available while
            // the real task-tracker SignalR negotiate/connect calls fail at the network
            // layer. A healthy HTTP backend must keep the application usable instead of
            // surfacing the blocking backend-offline overlay.
            var firstHubRequest = Page.WaitForRequestAsync(request => request.Url.Contains("/hubs/servers", StringComparison.Ordinal),
                new() { Timeout = 0 });
            await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            // Wait for the condition, not for a guessed duration: the booted WASM frontend opens its first
            // hub call when it is ready, which on a loaded agent takes longer than any fixed budget.
            // ResponseAsync settles once the route has aborted the call, so the counter is read after it.
            var hubRequest = await firstHubRequest.WaitAsync(TimeSpan.FromMilliseconds(PlaywrightConfig.AppReadyTimeoutMs));
            var hubResponse = await hubRequest.ResponseAsync();
            Assert.That(hubResponse, Is.Null, "The first SignalR request must be the one the route aborted.");

            Assert.That(Volatile.Read(ref blockedHubRequests), Is.GreaterThan(0),
                "The browser must have exercised a blocked SignalR request.");
            await Expect(dialog).ToHaveCountAsync(0);
        }
        finally
        {
            await Page.UnrouteAsync(taskHubPattern);
            // A real network recovery raises `online`, and the app now listens for it to cut the
            // current backoff step short. Raising it here is not a shortcut for the test: without
            // it the route silently becomes reachable again, which is exactly the situation the
            // browser reports and no code can otherwise detect. It is also the assertion that the
            // R5 wake-up wiring exists at all - remove the listener and this test fails on time.
            await Page.EvaluateAsync("() => window.dispatchEvent(new Event('online'))");
        }

        // Restoring the real SignalR route lets the initial-connect retry loop recover without
        // a reload or manual action.
        await Expect(dialog).ToHaveCountAsync(0, new() { Timeout = 15000 });
        await WaitForNoSpinnerAsync();

        // Prove that the restored connection transports a real server event, not only that the
        // overlay disappeared: task creation broadcasts TaskQueued and the header tracker renders it.
        var taskName = $"e2e-reconnect-{Guid.NewGuid():N}";
        var taskId = 0;
        try
        {
            taskId = await Page.EvaluateAsync<int>(
                """
                async ({ backendUrl, serverId, taskName }) => {
                    const token = localStorage.getItem('aetheus_auth_token');
                    const response = await fetch(`${backendUrl}/api/tasks`, {
                        method: 'POST',
                        headers: {
                            'Authorization': `Bearer ${token}`,
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify({
                            serverId,
                            name: taskName,
                            command: 'echo e2e-reconnect',
                            executor: 'Shell',
                            timeoutSeconds: 30,
                            environmentVariables: {}
                        })
                    });
                    if (!response.ok) throw new Error(`Task creation failed: HTTP ${response.status}`);
                    return (await response.json()).id;
                }
                """,
                new { backendUrl = BackendUrl, serverId, taskName });

            // The simulated online event wakes the reconnect loop, so the timeout covers connection
            // and state recovery. The probe task stays unfinished without an agent, keeping its
            // badge observable throughout recovery.
            await Expect(Page.Locator(".task-tracker-count")).ToBeVisibleAsync(new() { Timeout = 30000 });

            // The overlay can come back once more here: SignalR backs off between reconnect
            // attempts, so the longer the hub stayed blocked the later the successful retry lands.
            // Clicking through it failed with "the connection overlay intercepts pointer events".
            // Re-asserting its absence is stricter than ignoring it - the click below still has to
            // happen on a genuinely reconnected page.
            await Expect(dialog).ToHaveCountAsync(0, new() { Timeout = 30000 });
            // OE 1.2.0 puts the popover Id on its trigger and sets aria-controls only while the panel
            // exists, so the closed trigger is found by its id and the link is checked once open.
            var trackerButton = Page.Locator("button#task-tracker.task-tracker-btn");
            await trackerButton.ClickAsync();
            await Expect(trackerButton).ToHaveAttributeAsync("aria-controls", "task-tracker-panel");
            await Expect(Page.Locator(".task-tracker-row-name").Filter(new() { HasText = taskName }))
                .ToBeVisibleAsync(new() { Timeout = 10000 });
        }
        finally
        {
            if (taskId > 0)
            {
                await AssertTaskCancelledOrTerminalAsync(
                    taskId,
                    "The reconnect probe task must be cancelled or already terminal.");
            }
        }
    }
}
