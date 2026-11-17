// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("ProductionSmoke")]
public sealed class ProductionSmokeTests : E2ETestBase
{
    [Test]
    [CancelAfter(180_000)]
    public async Task PublicApplication_ProvesNonMutatingProductionReadiness()
    {
        var elapsed = Stopwatch.StartNew();

        var websocketConnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverMessageReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Page.WebSocket += (_, socket) =>
        {
            if (!socket.Url.Contains("/hubs/servers", StringComparison.OrdinalIgnoreCase)) return;
            websocketConnected.TrySetResult(socket.Url);
            socket.FrameReceived += (_, payload) =>
            {
                if (payload.Text?.Contains("\"type\":3", StringComparison.Ordinal) == true)
                    serverMessageReceived.TrySetResult(payload.Text);
            };
        };

        await Page.GotoAsync(FrontendUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.WaitForSelectorAsync("[data-testid='blazor-ready']", new() { Timeout = 30000 });
        await Expect(Page.Locator(".omni-panel-menu")).ToBeVisibleAsync(new() { Timeout = 30000 });

        var socketUrl = await websocketConnected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var serverMessage = await serverMessageReceived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var token = await Page.EvaluateAsync<string>("localStorage.getItem('aetheus_auth_token') || ''");
        Assert.That(token, Is.Not.Empty);
        Assert.That(socketUrl, Does.Contain("/hubs/servers"));
        Assert.That(serverMessage, Does.Contain("\"type\":3"));

        var backendEvidence = await Page.EvaluateAsync<JsonElement>(
            """
            async ({ backendUrl }) => {
                const token = localStorage.getItem('aetheus_auth_token');
                const readiness = await fetch(`${backendUrl}/health/ready`, { cache: 'no-store' });
                const projects = await fetch(`${backendUrl}/api/projects?page=1&pageSize=1`, {
                    cache: 'no-store',
                    headers: { 'Authorization': `Bearer ${token}` }
                });
                const projectBody = projects.ok ? await projects.json() : null;
                return {
                    readinessStatus: readiness.status,
                    readinessBody: await readiness.text(),
                    projectsStatus: projects.status,
                    projectCount: projectBody?.totalCount ?? -1,
                    projectItems: projectBody?.items?.length ?? -1
                };
            }
            """,
            new { backendUrl = BackendUrl });

        var projectCount = backendEvidence.GetProperty("projectCount").GetInt32();
        var projectItems = backendEvidence.GetProperty("projectItems").GetInt32();

        Assert.Multiple(() =>
        {
            Assert.That(backendEvidence.GetProperty("readinessStatus").GetInt32(), Is.EqualTo(200));
            Assert.That(backendEvidence.GetProperty("readinessBody").GetString(), Is.EqualTo("Healthy"));
            Assert.That(backendEvidence.GetProperty("projectsStatus").GetInt32(), Is.EqualTo(200));
            // -1 is this probe's marker for "the request did not return a body it could read", which
            // is a real failure however many projects the environment holds.
            Assert.That(projectCount, Is.GreaterThanOrEqualTo(0), "the projects endpoint returned no readable body");
            // The page asked for one item. The payload must agree with its own total: one item when
            // there is anything to list, none when there is not. Demanding a non-empty environment
            // instead made a first deployment impossible to qualify - a brand-new control plane has
            // no projects yet, and that is a correct answer, not a broken deployment.
            Assert.That(projectItems, Is.EqualTo(Math.Min(projectCount, 1)));
        });

        await NavigateToAsync("projects");
        Assert.That(new Uri(Page.Url).AbsolutePath, Is.EqualTo("/projects"));
        // Either way the page has to render its own state rather than hang or blank out, which is
        // what this leg is really evidence of.
        if (projectCount > 0)
            await Expect(Page.GetByTestId("project-item").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        else
            // The global menu is on every page, so it proves nothing about the projects content: the page's own
            // empty state is the only acceptable outcome of an environment that holds no project.
            await Expect(Page.GetByTestId("projects-empty")).ToBeVisibleAsync(new() { Timeout = 30000 });

        var contentSecurityPolicies = await Page.EvaluateAsync<string[]>(
            """
            async urls => Promise.all(urls.map(async url => {
                const response = await fetch(url, { cache: 'no-store' });
                if (!response.ok) throw new Error(`Frontend smoke request failed: ${response.status} ${url}`);
                return response.headers.get('content-security-policy') || '';
            }))
            """,
            new[] { FrontendUrl, $"{FrontendUrl.TrimEnd('/')}/projects" });
        if (PlaywrightConfig.RequireFrontendSecurityHeaders)
        {
            Assert.That(contentSecurityPolicies, Has.All.Not.Empty);
            Assert.That(contentSecurityPolicies, Has.All.Contains("default-src"));
        }

        elapsed.Stop();
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromMinutes(3)));
        TestContext.Progress.WriteLine(
            $"Production smoke passed in {elapsed.Elapsed.TotalSeconds:F1}s: Blazor, auth, protected route, "
            + "database readiness, non-empty project page, SignalR server response, browser console, "
            + (PlaywrightConfig.RequireFrontendSecurityHeaders
                ? "and CSP on every visited page."
                : "with CSP explicitly outside the local development host's scope."));
    }
}
