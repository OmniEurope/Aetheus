// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-26 (user decision 2026-11-05) on the real host and PostgreSQL: the pipeline setup wizard prepares the collection of the
/// project's monitored application through <c>POST /api/pipelines/setup/collection</c>. It creates what is missing (the
/// enabled application, its ingestion key, the audience configuration with the origin read from the production library,
/// the pseudonymization key in the project's vault), changes nothing on a second call, tells a missing production address
/// instead of inventing one, and is refused without Write on the project.
/// </summary>
public sealed class PipelineSetupCollectionIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/pipelines/setup/collection";
    private const string Domain = "wizard-collection.example.com";

    private static object Request(int projectId, bool telemetry = true, bool audience = true) =>
        new { projectId, telemetry, audience };

    private static async Task SeedProductionDomainAsync(IServiceProvider services, RbacAuditSeed.Tenant tenant, string? domain)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var projectName = await db.Projects.Where(project => project.Id == tenant.ProjectId).Select(project => project.Name).SingleAsync(TestContext.Current.CancellationToken);
        var slug = ProjectResourceNames.Slug(projectName, tenant.ProjectId);
        var share = new VariableLibrary { Name = $"{slug}.share", ProjectId = tenant.ProjectId };
        share.Entries.Add(new VariableLibraryEntry { Key = "APP_SUBDOMAIN", Value = string.Empty });
        db.VariableLibraries.Add(share);
        if (domain is not null)
        {
            var prod = new VariableLibrary { Name = $"{slug}.prod", ProjectId = tenant.ProjectId };
            prod.Entries.Add(new VariableLibraryEntry { Key = "DOMAIN", Value = domain });
            db.VariableLibraries.Add(prod);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Dictionary<string, (string Outcome, string? Detail)>> ReadItemsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Result>(IntegrationJsonOptions.Default, TestContext.Current.CancellationToken);
        return result!.Items.ToDictionary(item => item.Kind, item => (item.Outcome, item.Detail), StringComparer.Ordinal);
    }

    private sealed record Item(string Kind, string Outcome, string? Detail);

    private sealed record Result(List<Item> Items);

    [Fact]
    public async Task Prepare_CreatesWhatIsMissing_ThenAnIdenticalSecondCallChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "coll");
        await SeedProductionDomainAsync(factory.Services, tenant, Domain);
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "coll-writer", (RoleNames.Contributor, tenant.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var first = await ReadItemsAsync(await client.PostAsJsonAsync(Route, Request(tenant.ProjectId), ct));

        Assert.Equal(7, first.Count);
        Assert.All(first.Values, item => Assert.Equal("Done", item.Outcome));
        Assert.Equal($"https://{Domain}", first["Origins"].Detail);
        Assert.False(string.IsNullOrWhiteSpace(first["SiteId"].Detail));

        MonitoredApp stored;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            stored = await db.MonitoredApps.AsNoTracking().SingleAsync(app => app.ProjectId == tenant.ProjectId, ct);
            Assert.True(stored.Enabled);
            Assert.True(stored.TelemetrySetupRequested);
            Assert.NotNull(stored.IngestKeyHash);
            Assert.True(stored.AnalyticsEnabled);
            Assert.True(stored.AnalyticsPublicIngestEnabled);
            Assert.Equal(first["SiteId"].Detail, stored.AnalyticsSiteId);
            Assert.Contains($"https://{Domain}", stored.AnalyticsAllowedOriginsJson!, StringComparison.Ordinal);
            Assert.Equal(1, stored.AnalyticsPseudonymKeyVersion);
            Assert.NotNull(stored.AnalyticsVaultName);
            // The pseudonymization key really sits in the project's vault, and the ingestion key and the configuration are audited.
            var vault = await db.Vaults.AsNoTracking().Include(item => item.Secrets).SingleAsync(item => item.Name == stored.AnalyticsVaultName, ct);
            Assert.Equal(tenant.ProjectId, vault.ProjectId);
            Assert.Single(vault.Secrets);
            Assert.Contains(await db.AuditLogs.Where(log => log.EntityType == "MonitoredApp" && log.EntityId == stored.Id).Select(log => log.Action).ToListAsync(ct),
                action => action == "CreatedIngestKey");
            Assert.Contains(await db.AuditLogs.Where(log => log.EntityType == "MonitoredApp" && log.EntityId == stored.Id).Select(log => log.Action).ToListAsync(ct),
                action => action == "ConfiguredWebAnalytics");
        }

        var second = await ReadItemsAsync(await client.PostAsJsonAsync(Route, Request(tenant.ProjectId), ct));

        Assert.Equal(7, second.Count);
        Assert.All(second.Values, item => Assert.Equal("AlreadyPresent", item.Outcome));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var again = await db.MonitoredApps.AsNoTracking().SingleAsync(app => app.ProjectId == tenant.ProjectId, ct);
            Assert.Equal(stored.Id, again.Id);
            // No rotation: the key, its version, the pseudonymization key version and the site are the ones of the first call.
            Assert.Equal(stored.IngestKeyHash, again.IngestKeyHash);
            Assert.Equal(stored.IngestKeyVersion, again.IngestKeyVersion);
            Assert.Equal(stored.AnalyticsPseudonymKeyVersion, again.AnalyticsPseudonymKeyVersion);
            Assert.Equal(stored.AnalyticsSiteId, again.AnalyticsSiteId);
            Assert.Equal(stored.AnalyticsAllowedOriginsJson, again.AnalyticsAllowedOriginsJson);
            Assert.Single(await db.Vaults.Where(item => item.Name == stored.AnalyticsVaultName).ToListAsync(ct));
        }
    }

    [Fact]
    public async Task Prepare_WithoutAProductionDomain_PreparesTheTechnicalPart_AndSaysTheAudienceCouldNotBe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "nodom");
        await SeedProductionDomainAsync(factory.Services, tenant, domain: null);
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "nodom-writer", (RoleNames.Contributor, tenant.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var items = await ReadItemsAsync(await client.PostAsJsonAsync(Route, Request(tenant.ProjectId), ct));

        Assert.Equal("Done", items["AppEnabled"].Outcome);
        Assert.Equal("Done", items["IngestKey"].Outcome);
        foreach (var kind in new[] { "AudienceEnabled", "SiteId", "Origins", "PublicIngest", "PseudonymKey" })
        {
            Assert.Equal("Failed", items[kind].Outcome);
            Assert.Contains("production address", items[kind].Detail, StringComparison.Ordinal);
        }
        using var scope = factory.Services.CreateScope();
        var app = await scope.ServiceProvider.GetRequiredService<AppDbContext>().MonitoredApps.AsNoTracking()
            .SingleAsync(item => item.ProjectId == tenant.ProjectId, ct);
        Assert.False(app.AnalyticsEnabled);
        Assert.Null(app.AnalyticsVaultName);
    }

    [Fact]
    public async Task Prepare_TelemetryOnly_LeavesTheAudienceAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "telonly");
        await SeedProductionDomainAsync(factory.Services, tenant, Domain);
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "telonly-writer", (RoleNames.Contributor, tenant.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var items = await ReadItemsAsync(await client.PostAsJsonAsync(Route, Request(tenant.ProjectId, audience: false), ct));

        Assert.Equal(["AppEnabled", "IngestKey"], items.Keys.Order(StringComparer.Ordinal).ToArray());
        using var scope = factory.Services.CreateScope();
        var app = await scope.ServiceProvider.GetRequiredService<AppDbContext>().MonitoredApps.AsNoTracking()
            .SingleAsync(item => item.ProjectId == tenant.ProjectId, ct);
        Assert.False(app.AnalyticsEnabled);
    }

    [Fact]
    public async Task Prepare_WithoutWriteOnTheProject_IsRefused_AndCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "ro");
        var other = await RbacAuditSeed.SeedTenantAsync(factory.Services, "other");
        await SeedProductionDomainAsync(factory.Services, tenant, Domain);
        var reader = await RbacAuditSeed.CreateUserAsync(factory.Services, "ro-reader", (RoleNames.Reader, tenant.OrganizationId));
        var outsider = await RbacAuditSeed.CreateUserAsync(factory.Services, "ro-outsider", (RoleNames.Contributor, other.OrganizationId));

        using var readerClient = await IntegrationUsers.LoginClientAsync(factory, reader.Username);
        using var outsiderClient = await IntegrationUsers.LoginClientAsync(factory, outsider.Username);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync(Route, Request(tenant.ProjectId), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.PostAsJsonAsync(Route, Request(tenant.ProjectId), ct)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.MonitoredApps.AnyAsync(app => app.ProjectId == tenant.ProjectId, ct));
        Assert.False(await db.Vaults.AnyAsync(vault => vault.ProjectId == tenant.ProjectId && vault.Name.EndsWith(".analytics"), ct));
    }

    [Fact]
    public async Task Prepare_AnonymousCaller_IsRejected()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Request(1), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
