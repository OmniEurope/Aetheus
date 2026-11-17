// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// ADR-021 phase 1: end-to-end coverage of the agent ownership guard on
/// <c>POST /api/appmonitoring/agent/probe-results</c>. An agent may only report results for apps hosted
/// on its own server; reporting for another server's app must be rejected (mirrors the LogsController guard).
/// </summary>
public sealed class AppMonitoringProbeGuardIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ReportProbeResults_ForeignServerApp_IsForbidden_OwnServerApp_Accepted()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        int appOnServerA;
        string serverBToken;
        string serverAToken;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

            var orgId = await db.Organizations.OrderBy(o => o.Id).Select(o => o.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            var project = new Project { Name = "mon-proj", OrganizationId = orgId };
            db.Projects.Add(project);

            var serverA = new Server { Name = "srvA", Hostname = "srvA.local", OrganizationId = orgId };
            var serverB = new Server { Name = "srvB", Hostname = "srvB.local", OrganizationId = orgId };
            db.Servers.AddRange(serverA, serverB);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var app = new MonitoredApp
            {
                ProjectId = project.Id,
                ServerId = serverA.Id,
                Name = "app-a",
                ProbeUrl = "http://localhost/health",
                Enabled = true
            };
            db.MonitoredApps.Add(app);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            appOnServerA = app.Id;

            serverAToken = (await auth.RotateAgentTokenAsync(serverA.Id, ct: TestContext.Current.CancellationToken))!.BearerToken;
            serverBToken = (await auth.RotateAgentTokenAsync(serverB.Id, ct: TestContext.Current.CancellationToken))!.BearerToken;
        }

        var batch = new List<AppProbeResultDto>
        {
            new() { MonitoredAppId = appOnServerA, IsUp = false, Timestamp = DateTime.UtcNow, Error = "conn refused" }
        };

        // Server B reporting for server A's app -> Forbidden.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serverBToken);
        var foreign = await client.PostAsJsonAsync("/api/appmonitoring/agent/probe-results", batch, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        // Server A reporting for its own app -> accepted.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serverAToken);
        var owned = await client.PostAsJsonAsync("/api/appmonitoring/agent/probe-results", batch, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, owned.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var samples = await db.AppHealthSamples.CountAsync(s => s.MonitoredAppId == appOnServerA, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, samples); // only the authorized report was persisted
        }
    }
}
