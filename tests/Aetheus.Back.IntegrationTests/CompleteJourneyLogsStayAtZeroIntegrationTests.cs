// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-1, the general guard "no warning on a complete journey": as the administrator, a real sign-in then every read the
/// front makes on its main pages (home, projects and one project, pipelines and one pipeline and run, servers and one
/// server, vaults and variable libraries, analysis, administration), driven over real HTTP against PostgreSQL. The host
/// writes no line at Warning, Error or Critical for it, so none of the three classes of the 2026-10-29 export (an
/// application warning, a refused request, a slow request) can come back unseen. The slow request line of the host is a
/// Warning, so this guard also holds the "zero slow request" half of the row on the journey; the same journey names its
/// slowest requests and database commands, to read what stays close to the 1500 ms threshold.
/// </summary>
// Serial, on its own smoke host: the host's slow-connection probe and slow-request diagnostics read
// process-wide meters and timings, so a host of any class running beside it writes Warning lines here.
[Collection(SerialPostgresCollection.Name)]
public sealed class CompleteJourneyLogsStayAtZeroIntegrationTests(ApiSmokeFixture fixture) : IClassFixture<ApiSmokeFixture>
{
    /// <summary>The threshold of <c>SlowRequestDiagnosticsOptions.Threshold</c> (1500 ms).</summary>
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public async Task ACompleteAdministratorJourney_WritesNothingAtWarningOrAbove_AndNothingSlow()
    {
        var ct = TestContext.Current.CancellationToken;
        var warnings = fixture.Factory.Warnings;
        var commands = fixture.Factory.CommandTimings;
        var warningMark = warnings.Mark();
        var commandMark = commands.Mark();
        var requests = new List<(string Route, TimeSpan Elapsed)>();
        var failures = new List<string>();

        // 1. Sign in, as everyone does.
        using var client = fixture.Factory.CreateClient();
        var watch = Stopwatch.StartNew();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, ct));
        requests.Add(("POST /api/auth/login", watch.Elapsed));

        // 2. A vault and a variable library that the detail pages read.
        var vaultId = await CreateAsync(client, "/api/vaults",
            new CreateVaultRequest { Name = $"journey-{Guid.NewGuid():N}", ProjectId = fixture.ProjectId }, ct);
        var libraryId = await CreateAsync(client, "/api/variable-libraries",
            new CreateVariableLibraryRequest { Name = $"journey-{Guid.NewGuid():N}"[..40], ProjectId = fixture.ProjectId }, ct);

        try
        {
            foreach (var template in Journey)
            {
                var route = template
                    .Replace("{server}", fixture.ServerId.ToString(), StringComparison.Ordinal)
                    .Replace("{project}", fixture.ProjectId.ToString(), StringComparison.Ordinal)
                    .Replace("{pipeline}", fixture.PipelineId.ToString(), StringComparison.Ordinal)
                    .Replace("{run}", fixture.PipelineRunId.ToString(), StringComparison.Ordinal)
                    .Replace("{vault}", vaultId.ToString(), StringComparison.Ordinal)
                    .Replace("{library}", libraryId.ToString(), StringComparison.Ordinal);
                watch.Restart();
                using var response = await client.GetAsync(route, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                requests.Add(("GET " + route, watch.Elapsed));
                // The project tracking read answers 204 while the project has no tracking yet; the front handles it as "none".
                var expected = route.EndsWith("/tracking", StringComparison.Ordinal) ? HttpStatusCode.NoContent : HttpStatusCode.OK;
                if (response.StatusCode != expected)
                    failures.Add($"GET {route} answered {(int)response.StatusCode} {response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
            }
        }
        finally
        {
            (await client.DeleteAsync($"/api/vaults/{vaultId}", ct)).EnsureSuccessStatusCode();
            (await client.DeleteAsync($"/api/variable-libraries/{libraryId}", ct)).EnsureSuccessStatusCode();
        }

        // A background loop that fails because of what the journey did writes its line a moment later.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        var timings = commands.Since(commandMark);
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Journey: {requests.Count} requests, {timings.Count} database commands.");
        foreach (var request in requests.OrderByDescending(request => request.Elapsed).Take(5))
            output?.WriteLine($"Slowest request: {request.Elapsed.TotalMilliseconds:F0} ms {request.Route}");
        foreach (var command in timings.OrderByDescending(command => command.Duration).Take(5))
            output?.WriteLine($"Slowest command: {command.Duration.TotalMilliseconds:F0} ms {command.Text}");

        Assert.True(failures.Count == 0, "The journey is not healthy, a read did not answer 200:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        var written = warnings.Since(warningMark);
        Assert.True(written.Count == 0, "The journey wrote Warning-or-above lines:" + Environment.NewLine + string.Join(Environment.NewLine, written));
        var slow = requests.Where(request => request.Elapsed >= SlowThreshold).ToArray();
        Assert.True(slow.Length == 0, "Slow requests on the journey: " + string.Join(", ", slow.Select(request => $"{request.Route} {request.Elapsed.TotalMilliseconds:F0} ms")));
        var slowCommands = timings.Where(command => command.Duration >= SlowThreshold).ToArray();
        Assert.True(slowCommands.Length == 0, "Slow database commands on the journey: " + string.Join(" | ", slowCommands.Select(command => $"{command.Duration.TotalMilliseconds:F0} ms {command.Text}")));
    }

    private static async Task<int> CreateAsync<TRequest>(HttpClient client, string route, TRequest request, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(route, request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedIdentity>(IntegrationJsonOptions.Default, ct);
        return created!.Id;
    }

    private sealed class CreatedIdentity
    {
        public int Id { get; set; }
    }

    /// <summary>The reads of the front's main pages (its <c>*Api.cs</c> clients), by page.</summary>
    private static readonly string[] Journey =
    [
        // Session and home.
        "/api/users/me", "/api/users/me/preferences", "/api/organizations/me", "/api/organizations/administered",
        "/api/notifications/me/unread-count", "/api/monitoring/dashboard", "/api/dashboards", "/api/alerts",
        "/api/appmonitoring/summary", "/api/tasks/active", "/api/pipelines/runs/active", "/api/pipelines/favorites",
        "/api/pipelines/approvals/pending",
        // Projects, and one project.
        "/api/projects?page=1&pageSize=25", "/api/projects/{project}", "/api/projects/{project}/activity?count=10",
        "/api/projects/{project}/release-series", "/api/projects/{project}/data-retention", "/api/work-items/board/{project}",
        "/api/work-items?projectId={project}", "/api/artifacts/project/{project}", "/api/gitgraph/project/{project}",
        "/api/releases?page=1&pageSize=25", "/api/pipelines?projectId={project}", "/api/appmonitoring/projects/{project}/apps",
        "/api/appmonitoring/projects/{project}/telemetry-setup", "/api/git/repos", "/api/git/connections", "/api/environments",
        // Pipelines, and one pipeline and one run.
        "/api/pipelines?page=1&pageSize=25", "/api/pipelines/{pipeline}", "/api/pipelines/{pipeline}/runs?pageSize=50",
        "/api/pipelines/{pipeline}/fleet-item", "/api/pipelines/fleet", "/api/pipelines/fleet/filter-values",
        "/api/pipelines/dependencies", "/api/pipelines/templates", "/api/pipelines/resource-usage",
        "/api/pipelines/ownership/candidates", "/api/pipelines/setup/unmet?projectId={project}",
        "/api/pipelines/runs/{run}", "/api/pipelines/runs/{run}/lineage", "/api/pipelines/runs/{run}/schema-tracking",
        "/api/pipelines/runs/{run}/queue",
        // Servers, and one server.
        "/api/servers?page=1&pageSize=25", "/api/servers/names", "/api/servers/{server}", "/api/servers/{server}/diagnostic",
        "/api/servers/{server}/modules", "/api/servers/{server}/apps", "/api/servers/{server}/module-links",
        "/api/servers/{server}/projects", "/api/servers/{server}/pipelines", "/api/servers/{server}/vaults",
        "/api/servers/{server}/variable-libraries", "/api/servers/{server}/tasks?page=1&pageSize=25",
        "/api/servers/{server}/releases", "/api/servers/{server}/logs", "/api/servers/{server}/certbot",
        "/api/servers/{server}/docker/containers", "/api/servers/{server}/apache", "/api/servers/{server}/mail",
        "/api/servers/{server}/portsentry", "/api/servers/{server}/rkhunter", "/api/servers/{server}/security-updates",
        "/api/servers/{server}/firewall", "/api/servers/{server}/host-vulnerabilities",
        // Vaults and variable libraries.
        "/api/vaults", "/api/vaults/names", "/api/vaults/filter-values", "/api/vaults/{vault}",
        "/api/variable-libraries", "/api/variable-libraries/names", "/api/variable-libraries/filter-values",
        "/api/variable-libraries/{library}", "/api/variable-libraries/{library}/entries",
        // Analysis.
        "/api/analysis/portfolio/projects", "/api/analysis/portfolio", "/api/analysis/portfolio/filter-values",
        "/api/analysis/cve-watch/overview", "/api/analysis/cve-watch/advisories", "/api/analysis/cve-watch?organizationId=1",
        "/api/analysis/policies/global", "/api/analysis/organizations/1/policies", "/api/analysis/runs/findings?runIds={run}",
        "/api/analysis/projects/{project}/summary", "/api/analysis/projects/{project}/findings",
        "/api/analysis/projects/{project}/metrics", "/api/analysis/projects/{project}/components",
        "/api/analysis/projects/{project}/reports", "/api/analysis/projects/{project}/policies",
        "/api/analysis/projects/{project}/exceptions", "/api/analysis/projects/{project}/tracking",
        "/api/analysis/projects/{project}/vulnerabilities", "/api/pipelines/projects/{project}/quality-trend?take=10",
        // Administration.
        "/api/users", "/api/users/roles", "/api/roles", "/api/organizations", "/api/agent-pools", "/api/audit",
        "/api/audit/actions", "/api/audit/entity-types", "/api/audit/verify-chain", "/api/system-logs/files",
        "/api/system-log-rules?organizationId=1", "/api/admin/performance", "/api/admin/database-tables",
        "/api/settings", "/api/settings/secrets", "/api/settings/daily-recap", "/api/backups", "/api/plugins",
        "/api/package-feeds", "/api/package-registry", "/api/service-connections", "/api/webhooks",
        "/api/auth/registration-tokens", "/api/personal-access-tokens", "/api/mail-templates", "/api/mail/mailboxes",
        "/api/ai/profiles", "/api/data-storage/projects", "/api/data-storage/reclaim", "/api/notifications/channels"
    ];
}
