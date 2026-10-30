// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("Projects")]
public class ProjectsTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    public async Task Projects_PageRendersExpectedControlsAndContent()
    {
        await NavigateToAsync("projects");
        await Expect(Page.GetByText("Projects").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Projects"));

        // Scope Create to page-header actions because the empty state can also expose a creation
        // button.
        var newButton = Page.Locator(".omni-page-header__actions").GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true });
        await Expect(newButton).ToBeVisibleAsync(new() { Timeout = 10000 });
        var searchBox = Page.Locator(".project-list-search");
        await Expect(searchBox).ToBeVisibleAsync(new() { Timeout = 10000 });

        await WaitForNoSpinnerAsync();

        // Either project cards exist or the EmptyState component renders.
        var cards = Page.Locator(".project-card");
        var emptyState = Page.Locator(".empty-state");

        var cardsCount = await cards.CountAsync();
        var emptyVisible = await emptyState.IsVisibleAsync();

        Assert.That(cardsCount > 0 || emptyVisible, Is.True, "Should show project cards or empty state");

        var dropdown = Page.Locator("select.omni-drop-down").First;
        await Expect(dropdown).ToBeVisibleAsync();
        // Assert that the header omits total and refresh controls because entity events already
        // refresh the project list.
        var header = Page.Locator(".omni-page-header");
        await Expect(header.Locator("button[title='Refresh']")).ToHaveCountAsync(0);
        await Expect(header.Locator(".omni-badge").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"\d+\s*total", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }))
            .ToHaveCountAsync(0);
    }

    [Test]
    [RollbackRelevant]
    public async Task Projects_NewProject_PersistsAndIsDeletedThroughTheUi()
    {
        var projectName = $"e2e-project-{Guid.NewGuid():N}";
        await NavigateToAsync("projects");
        try
        {
            await Page.Locator(".omni-page-header__actions").GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true }).ClickAsync();
            var createDialog = Page.Locator(".omni-dialog");
            await Expect(createDialog).ToBeVisibleAsync(new() { Timeout = 10000 });
            await createDialog.Locator("#project-form-name").FillAsync(projectName);
            await createDialog.Locator("#Description").FillAsync("E2E persistence probe");
            await createDialog.GetByRole(AriaRole.Button, new()
            {
                NameRegex = new System.Text.RegularExpressions.Regex("Create$")
            }).ClickAsync();

            var createdDialog = Page.Locator(".omni-dialog").Filter(new() { HasText = "Project created" });
            await Expect(createdDialog).ToBeVisibleAsync(new() { Timeout = 10000 });
            // R-30: the secondary choice opens the new project's overview. The deployed baseline (V-1 gate, payload
            // only) still offers "Stay here", which keeps the list; this test proves the write path on both.
            var openProject = createdDialog.GetByRole(AriaRole.Button, new() { Name = "Open project", Exact = true });
            System.Text.RegularExpressions.Match idMatch;
            if (NewSinceDeployedBaselineAttribute.TargetIsPayloadOnlyPrevious && await openProject.CountAsync() == 0)
            {
                await createdDialog.GetByRole(AriaRole.Button, new() { Name = "Stay here", Exact = true }).ClickAsync();
                var projectCard = Page.Locator("a.project-card-name").Filter(new() { HasText = projectName });
                await Expect(projectCard).ToBeVisibleAsync(new() { Timeout = 10000 });
                idMatch = System.Text.RegularExpressions.Regex.Match(await projectCard.GetAttributeAsync("href") ?? string.Empty, @"/projects/(\d+)");
            }
            else
            {
                await openProject.ClickAsync();
                await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/projects/\d+(/overview)?/?$"), new() { Timeout = 10000 });
                idMatch = System.Text.RegularExpressions.Regex.Match(Page.Url, @"/projects/(\d+)");
            }
            Assert.That(idMatch.Success, Is.True, "The new project's id must be readable from its page or its card.");
            var projectId = int.Parse(idMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            await NavigateToAsync($"projects/{projectId}/edit");
            await Expect(Page.Locator("#oe-pages-projects-projectdetailsections-projecteditsection-1")).ToHaveValueAsync(projectName, new() { Timeout = 10000 });
            await Page.GetByRole(AriaRole.Button, new()
            {
                NameRegex = new System.Text.RegularExpressions.Regex("Delete$")
            }).ClickAsync();
            // The deletion counts what it removes and asks for the project's name before its Delete
            // button wakes up.
            var deleteDialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Delete the project", Exact = true });
            await Expect(deleteDialog).ToBeVisibleAsync(new() { Timeout = 5000 });
            var submit = deleteDialog.Locator("#project-delete-submit");
            await Expect(submit).ToBeDisabledAsync(new() { Timeout = 10000 });
            await deleteDialog.Locator("#project-delete-confirm").FillAsync(projectName);
            await Expect(submit).ToBeEnabledAsync(new() { Timeout = 5000 });
            await submit.ClickAsync();

            // R-50 (1e03cc0c2): the deletion ends in its dialog, on what was deleted and what was not; its single
            // Close button is the way back to the projects list.
            var closeReport = deleteDialog.Locator(".dialog-footer").GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true });
            await Expect(closeReport).ToBeVisibleAsync(new() { Timeout = 30000 });
            await closeReport.ClickAsync();

            await Expect(Page).ToHaveURLAsync(
                new System.Text.RegularExpressions.Regex(@"/projects/?$"),
                new() { Timeout = 10000 });
            await Expect(Page.Locator("a.project-card-name").Filter(new() { HasText = projectName }))
                .ToHaveCountAsync(0, new() { Timeout = 10000 });
        }
        finally
        {
            // Failure-safe cleanup: if an assertion above interrupted the UI path after persistence,
            // remove the uniquely named probe through the authenticated browser session.
            await Page.EvaluateAsync(
                """
                async ({ backendUrl, projectName }) => {
                    const token = localStorage.getItem('aetheus_auth_token');
                    const headers = { 'Authorization': `Bearer ${token}` };
                    const list = await fetch(
                        `${backendUrl}/api/projects?page=1&pageSize=200&search=${encodeURIComponent(projectName)}`,
                        { headers });
                    if (!list.ok) throw new Error(`Project cleanup lookup failed: HTTP ${list.status}`);
                    const page = await list.json();
                    for (const project of page.items.filter(item => item.name === projectName)) {
                        const response = await fetch(`${backendUrl}/api/projects/${project.id}`, {
                            method: 'DELETE',
                            headers
                        });
                        if (response.status !== 204 && response.status !== 404)
                            throw new Error(`Project cleanup failed: HTTP ${response.status}`);
                    }
                }
                """,
                new { backendUrl = BackendUrl, projectName });
        }
    }

    [Test]
    [NewSinceDeployedBaseline("R-455")]
    public async Task TotoMonitoring_ShowsPersistedTelemetryAudienceAndPerformance()
    {
        var evidence = await SeedTotoMonitoringEvidenceAsync();

        await NavigateToAsync($"projects/{evidence.ProjectId}/monitoring?tab=telemetry");
        // A project section uses its section title and a linked project ancestor in the breadcrumb.
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Monitoring", Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        // The trail is OmniPageHeader's own, an OmniBreadcrumb navigation landmark named by OE.
        var projectCrumb = Page.GetByRole(AriaRole.Navigation, new() { Name = "Breadcrumb", Exact = true })
            .GetByRole(AriaRole.Link, new() { Name = evidence.ProjectName, Exact = true });
        await Expect(projectCrumb).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(projectCrumb).ToHaveAttributeAsync("href", $"/projects/{evidence.ProjectId}/overview");
        await Expect(Page.GetByText("Telemetry", new() { Exact = true }).Last)
            .ToBeVisibleAsync(new() { Timeout = 10000 });

        var applicationDropdown = Page.Locator("select.omni-drop-down").First;
        await applicationDropdown.SelectOptionAsync(new SelectOptionValue { Label = evidence.ApplicationName });
        await Expect(applicationDropdown).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(".+"));

        await Expect(Page.GetByText("Unique visitors today", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page.GetByText("Browser performance samples (30 days)", new() { Exact = true }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByText("Average browser navigation", new() { Exact = true }))
            .ToBeVisibleAsync();

        // Top-page telemetry has its own dedicated test so this scenario remains applicable to a
        // preceding release without that tab.

        // Open the Performance metric disclosure before inspecting its lazily rendered chart.
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Performance", Exact = true }).ClickAsync();
        await Page.Locator("summary.omni-fieldset__summary", new() { HasText = "Explore a metric" }).ClickAsync();
        var metricChart = Page.Locator(".omni-chart:visible").First;
        await Expect(metricChart.Locator("title[id$='-title']")).ToHaveTextAsync(
            "aetheus.e2e.real.browser",
            new() { Timeout = 10000 });
        await Expect(metricChart).ToBeVisibleAsync(new() { Timeout = 10000 });

        await Page.GetByRole(AriaRole.Tab, new() { Name = "Logs", Exact = true }).ClickAsync();
        await Expect(Page.GetByText(evidence.LogMarker, new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Test]
    [NewSinceDeployedBaseline("R-355")]
    public async Task TotoMonitoring_TopPagesHaveTheirOwnTelemetryTab()
    {
        var evidence = await SeedTotoMonitoringEvidenceAsync();

        await NavigateToAsync($"projects/{evidence.ProjectId}/monitoring?tab=telemetry");
        var applicationDropdown = Page.Locator("select.omni-drop-down").First;
        await Expect(applicationDropdown).ToBeVisibleAsync(new() { Timeout = 10000 });
        await applicationDropdown.SelectOptionAsync(new SelectOptionValue { Label = evidence.ApplicationName });
        await Expect(Page.GetByText("Unique visitors today", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });

        await Page.GetByRole(AriaRole.Tab, new() { Name = "Top pages", Exact = true }).ClickAsync();
        await Expect(Page.GetByText("/e2e/real-observability", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    private static async Task<TotoMonitoringEvidence> SeedTotoMonitoringEvidenceAsync()
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        using var storageState = JsonDocument.Parse(E2EAuthSession.StorageStateJson!);
        var token = storageState.RootElement.GetProperty("origins")[0]
            .GetProperty("localStorage")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "aetheus_auth_token")
            .GetProperty("value")
            .GetString();
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var projects = await http.GetFromJsonAsync<ProjectPageEvidence>(
            $"{BackendUrl}/api/projects?page=1&pageSize=200&search=Toto");
        var project = projects?.Items.FirstOrDefault(item =>
                item.Name.Contains("Toto", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The seeded Toto project was not found.");
        var apps = await http.GetFromJsonAsync<List<MonitoredAppEvidence>>(
            $"{BackendUrl}/api/appmonitoring/projects/{project.Id}/apps");
        var app = apps?.FirstOrDefault(item => item.Name == "toto-accept")
            ?? throw new InvalidOperationException("Toto's monitored application 'toto-accept' was not found.");

        using var keyResponse = await http.PostAsync(
            $"{BackendUrl}/api/appmonitoring/apps/{app.Id}/ingest-key",
            content: null);
        keyResponse.EnsureSuccessStatusCode();
        var ingestKey = await keyResponse.Content.ReadFromJsonAsync<IngestKeyEvidence>()
            ?? throw new InvalidOperationException("An ingestion key was not returned.");
        ArgumentException.ThrowIfNullOrWhiteSpace(ingestKey.Key);

        using var configurationResponse = await http.PutAsJsonAsync(
            $"{BackendUrl}/api/appmonitoring/apps/{app.Id}/web-analytics/configuration",
            new
            {
                enabled = true,
                publicIngestEnabled = true,
                siteId = app.Name,
                allowedOrigins = new[] { "https://e2e.aetheus.invalid" },
                storageBudgetBytes = 104_857_600
            });
        configurationResponse.EnsureSuccessStatusCode();
        var configuration = await configurationResponse.Content
            .ReadFromJsonAsync<WebAnalyticsConfigurationEvidence>()
            ?? throw new InvalidOperationException("Web analytics configuration was not returned.");
        Assert.That(configuration.PseudonymKeyVersion, Is.GreaterThan(0));

        var now = DateTimeOffset.UtcNow;
        var nowNanos = now.ToUnixTimeMilliseconds() * 1_000_000L;
        await SendIngestAsync(
            "api/ingest/otlp/v1/metrics",
            new
            {
                resourceMetrics = new[]
                {
                    new
                    {
                        scopeMetrics = new[]
                        {
                            new
                            {
                                metrics = new[]
                                {
                                    new
                                    {
                                        name = "aetheus.e2e.real.browser",
                                        unit = "ms",
                                        gauge = new
                                        {
                                            dataPoints = new[]
                                            {
                                                new { asDouble = 187, timeUnixNano = (nowNanos - 1_000_000_000L).ToString() },
                                                new { asDouble = 191, timeUnixNano = nowNanos.ToString() }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });

        var logMarker = $"toto-e2e-real-{Guid.NewGuid():N}";
        await SendIngestAsync(
            "api/ingest/otlp/v1/logs",
            new
            {
                resourceLogs = new[]
                {
                    new
                    {
                        scopeLogs = new[]
                        {
                            new
                            {
                                logRecords = new[]
                                {
                                    new
                                    {
                                        timeUnixNano = nowNanos.ToString(),
                                        severityNumber = 9,
                                        severityText = "Information",
                                        body = new { stringValue = logMarker }
                                    }
                                }
                            }
                        }
                    }
                }
            });

        var seed = Guid.NewGuid().ToString("N");
        var daily = Hash($"daily:{seed}");
        var weekly = Hash($"weekly:{seed}");
        var monthly = Hash($"monthly:{seed}");
        var session = Hash($"session:{seed}");
        await SendIngestAsync("api/ingest/visitors", new { visitorId = daily });
        await SendIngestAsync(
            "api/ingest/web-analytics/v1/events",
            new[]
            {
                new WebAnalyticsEventEvidence
                {
                    SchemaVersion = 1,
                    ApplicationId = app.Id,
                    SiteId = app.Name,
                    EventId = Guid.NewGuid(),
                    OccurredAtUtc = now.UtcDateTime,
                    Kind = "page_view",
                    Route = "/e2e/real-observability",
                    DailyPseudonym = daily,
                    WeeklyPseudonym = weekly,
                    MonthlyPseudonym = monthly,
                    SessionPseudonym = session,
                    KeyVersion = configuration.PseudonymKeyVersion
                },
                new WebAnalyticsEventEvidence
                {
                    SchemaVersion = 1,
                    ApplicationId = app.Id,
                    SiteId = app.Name,
                    EventId = Guid.NewGuid(),
                    OccurredAtUtc = now.UtcDateTime,
                    Kind = "browser_performance",
                    Route = "/e2e/real-observability",
                    DurationMs = 187,
                    DailyPseudonym = daily,
                    WeeklyPseudonym = weekly,
                    MonthlyPseudonym = monthly,
                    SessionPseudonym = session,
                    KeyVersion = configuration.PseudonymKeyVersion
                }
            });

        return new TotoMonitoringEvidence(project.Id, project.Name, app.Name, logMarker);

        async Task SendIngestAsync(string path, object payload)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BackendUrl}/{path}")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("x-aetheus-ingest-key", ingestKey.Key);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        static string Hash(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();
    }

    private sealed record TotoMonitoringEvidence(
        int ProjectId,
        string ProjectName,
        string ApplicationName,
        string LogMarker);

    private sealed record ProjectPageEvidence
    {
        public List<ProjectEvidence> Items { get; init; } = [];
    }

    private sealed record ProjectEvidence
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed record MonitoredAppEvidence
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed record IngestKeyEvidence
    {
        public string Key { get; init; } = string.Empty;
    }

    private sealed record WebAnalyticsConfigurationEvidence
    {
        public int PseudonymKeyVersion { get; init; }
    }

    private sealed record WebAnalyticsEventEvidence
    {
        public int SchemaVersion { get; init; }
        public int ApplicationId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public Guid EventId { get; init; }
        public DateTime OccurredAtUtc { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string Route { get; init; } = string.Empty;
        public int? DurationMs { get; init; }
        public string DailyPseudonym { get; init; } = string.Empty;
        public string WeeklyPseudonym { get; init; } = string.Empty;
        public string MonthlyPseudonym { get; init; } = string.Empty;
        public string SessionPseudonym { get; init; } = string.Empty;
        public int KeyVersion { get; init; }
    }
}
