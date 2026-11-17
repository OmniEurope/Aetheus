// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The end-of-life flag of the recommendation (PLAN-004 lot 5, point 5) through the real HTTP pipeline, JWT and
/// PostgreSQL: a runtime or a base image whose endoflife.date cycle has ended, with no fix to move to, is told
/// <c>EndOfLife</c>; one whose cycle is supported, unknown or floating is told <c>NoFixYet</c>, never out of support.
/// The cycles are the dates endoflife.date publishes for Node.js 18 and 20 (both past), plus a synthetic Node.js 22
/// that ends in 2099 so the test does not depend on the day it runs. The advisories are synthetic. The only double is
/// the network, which must stay untouched: every request of the "package-feeds" client is counted.
/// </summary>
public sealed class CveWatchEndOfLifeHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    // Affected from the first release, no fix stated: nothing to move to inside the advisory.
    private const string NoFix = """[{"type":"ECOSYSTEM","events":[{"introduced":"0"}]}]""";

    private sealed record Seed(int OrganizationId, int ProjectId, int InventoryId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> Host(AetheusWebApplicationFactory factory, NetworkDouble network) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("package-feeds").ConfigurePrimaryHttpMessageHandler(() => network)));

    [Fact]
    public async Task TheList_TellsEndOfLife_OnlyWhereTheCycleOfTheItemIsKnownToHaveEnded()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var network = new NetworkDouble();
        await using var host = Host(factory, network);
        await StoreCyclesAsync(host);
        var seed = await SeedProjectAsync(host);
        await AddMatchAsync(host, seed, "runtime", "nodejs", "18.19.0", ProjectInventoryItemKind.Runtime);
        await AddMatchAsync(host, seed, "docker", "docker.io/library/node", "20-alpine", ProjectInventoryItemKind.BaseImage, normalizedName: "node");
        await AddMatchAsync(host, seed, "runtime", "nodejs", "22.1.0", ProjectInventoryItemKind.Runtime);
        await AddMatchAsync(host, seed, "runtime", "nodejs", "999.0.0", ProjectInventoryItemKind.Runtime);
        await AddMatchAsync(host, seed, "runtime", "nodejs", ">=18", ProjectInventoryItemKind.Runtime);
        await AddMatchAsync(host, seed, "docker", "node", "latest", ProjectInventoryItemKind.BaseImage);
        using var admin = await IntegrationUsers.AdminClientAsync(host);

        var page = await ReadPageAsync(admin, seed.OrganizationId);

        Assert.Equal(CveRecommendationKind.EndOfLife, KindOf(page, "nodejs", "18.19.0"));
        Assert.Equal(CveRecommendationKind.EndOfLife, KindOf(page, "node", "20-alpine"));
        Assert.Equal(CveRecommendationKind.NoFixYet, KindOf(page, "nodejs", "22.1.0"));
        Assert.Equal(CveRecommendationKind.NoFixYet, KindOf(page, "nodejs", "999.0.0"));
        Assert.Equal(CveRecommendationKind.NoFixYet, KindOf(page, "nodejs", ">=18"));
        Assert.Equal(CveRecommendationKind.NoFixYet, KindOf(page, "node", "latest"));
        Assert.Empty(network.Requests);
    }

    [Fact]
    public async Task TheDetail_TellsEndOfLife_Too_WithoutAskingTheRegistry()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var network = new NetworkDouble();
        await using var host = Host(factory, network);
        await StoreCyclesAsync(host);
        var seed = await SeedProjectAsync(host);
        var ended = await AddMatchAsync(host, seed, "runtime", "nodejs", "18.19.0", ProjectInventoryItemKind.Runtime);
        var supported = await AddMatchAsync(host, seed, "runtime", "nodejs", "22.1.0", ProjectInventoryItemKind.Runtime);
        using var admin = await IntegrationUsers.AdminClientAsync(host);

        Assert.Equal(CveRecommendationKind.EndOfLife, (await ReadDetailAsync(admin, ended)).Recommendation?.Kind);
        Assert.Equal(CveRecommendationKind.NoFixYet, (await ReadDetailAsync(admin, supported)).Recommendation?.Kind);
        Assert.Empty(network.Requests);
    }

    [Fact]
    public async Task TheEndOfLifeAdvisoryItself_IsEndOfLife_WithNoCycleToMoveTo()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory, new NetworkDouble());
        await StoreCyclesAsync(host);
        var seed = await SeedProjectAsync(host);
        // What the end-of-life importer stores: 18 up to the next cycle, 19, which names no release to move to.
        const string cycleRange = """[{"type":"ECOSYSTEM","events":[{"introduced":"18"},{"fixed":"19"}]}]""";
        await AddMatchAsync(host, seed, "runtime", "nodejs", "18.19.0", ProjectInventoryItemKind.Runtime,
            ranges: cycleRange, externalId: $"EOL-nodejs-18-{Guid.NewGuid():N}");
        using var admin = await IntegrationUsers.AdminClientAsync(host);

        var page = await ReadPageAsync(admin, seed.OrganizationId);

        var recommendation = Assert.IsType<CveWatchRecommendationDto>(Assert.Single(page.Items).Recommendation);
        Assert.Equal(CveRecommendationKind.EndOfLife, recommendation.Kind);
        Assert.Null(recommendation.TargetVersion);
    }

    [Fact]
    public async Task TheList_ReadsTheCyclesOnce_WhateverTheNumberOfRows()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = Host(factory, new NetworkDouble());
        await StoreCyclesAsync(host);
        using var counter = new SqlCommandCounter();
        var small = await SeedProjectAsync(host);
        var large = await SeedProjectAsync(host);
        for (var patch = 0; patch < 2; patch++)
            await AddMatchAsync(host, small, "runtime", "nodejs", $"18.19.{patch}", ProjectInventoryItemKind.Runtime);
        for (var patch = 0; patch < 12; patch++)
            await AddMatchAsync(host, large, "runtime", "nodejs", $"18.19.{patch}", ProjectInventoryItemKind.Runtime);

        await CountPageAsync(counter, host, small.OrganizationId);
        var smallPage = await CountPageAsync(counter, host, small.OrganizationId);
        var largePage = await CountPageAsync(counter, host, large.OrganizationId);

        Assert.Equal(12, largePage.Result.Items.Count);
        Assert.Equal(2, smallPage.Result.Items.Count);
        Assert.All(largePage.Result.Items, item => Assert.Equal(CveRecommendationKind.EndOfLife, item.Recommendation?.Kind));
        Assert.Equal(smallPage.Commands, largePage.Commands);
    }

    private static CveRecommendationKind? KindOf(PaginatedResult<CveWatchMatchDto> page, string name, string version) =>
        page.Items.Single(item => item.PackageName == name && item.Version == version).Recommendation?.Kind;

    private static async Task<SqlCommandCounter.Measure<PaginatedResult<CveWatchMatchDto>>> CountPageAsync(
        SqlCommandCounter counter, WebApplicationFactory<Program> host, int organizationId) =>
        await counter.MeasureAsync(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICveWatchQueryService>()
                .GetPageAsync(organizationId, null, null, false, 1, 50, Ct);
        });

    private static async Task<CveWatchMatchDto> ReadDetailAsync(HttpClient client, long matchId)
    {
        var response = await client.GetAsync($"/api/analysis/cve-watch/{matchId}", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<CveWatchMatchDto>(IntegrationJsonOptions.Default, Ct)
            ?? throw new InvalidOperationException("The detail came back empty.");
    }

    private static async Task<PaginatedResult<CveWatchMatchDto>> ReadPageAsync(HttpClient client, int organizationId)
    {
        var response = await client.GetAsync($"/api/analysis/cve-watch?organizationId={organizationId}&pageSize=50", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<PaginatedResult<CveWatchMatchDto>>(IntegrationJsonOptions.Default, Ct)
            ?? throw new InvalidOperationException("The page came back empty.");
    }

    private static async Task StoreCyclesAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.VulnerabilityEndOfLifeCycles.Where(cycle => cycle.Product == "nodejs").ExecuteDeleteAsync(Ct);
        db.VulnerabilityEndOfLifeCycles.AddRange(
            new VulnerabilityEndOfLifeCycle { Product = "nodejs", Cycle = "18", EndOfLifeOn = new DateOnly(2025, 4, 30) },
            new VulnerabilityEndOfLifeCycle { Product = "nodejs", Cycle = "20", EndOfLifeOn = new DateOnly(2026, 4, 30) },
            new VulnerabilityEndOfLifeCycle { Product = "nodejs", Cycle = "22", EndOfLifeOn = new DateOnly(2099, 1, 1) });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<Seed> SeedProjectAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"eol-{Guid.NewGuid():N}", Slug = $"e{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = $"end-of-life-{Guid.NewGuid():N}", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        var inventory = new ProjectInventory
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            Origin = ProjectInventoryOrigin.Repository,
            ReadAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
            ChangedAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc)
        };
        db.ProjectInventories.Add(inventory);
        await db.SaveChangesAsync(Ct);
        return new Seed(organization.Id, project.Id, inventory.Id);
    }

    /// <summary>An advisory, the inventory item that holds the package, and their match.</summary>
    private static async Task<long> AddMatchAsync(
        WebApplicationFactory<Program> factory, Seed seed, string ecosystem, string name, string version, ProjectInventoryItemKind kind,
        string ranges = NoFix, string? externalId = null, string? normalizedName = null)
    {
        var stored = normalizedName ?? name;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var advisory = new VulnerabilityAdvisory
        {
            Source = "NODEJS",
            ExternalId = externalId ?? $"NODEJS-{Guid.NewGuid():N}"[..24],
            PrimaryCveId = "CVE-2099-0042",
            Severity = AnalysisSeverity.High,
            SeverityPublished = true,
            ModifiedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            AffectedPackages = [new VulnerabilityAffectedPackage { Ecosystem = ecosystem, PackageName = stored, RangesJson = ranges }]
        };
        var item = new ProjectInventoryItem
        {
            OrganizationId = seed.OrganizationId,
            ProjectId = seed.ProjectId,
            ProjectInventoryId = seed.InventoryId,
            Ecosystem = ecosystem,
            PackageName = stored,
            Version = version,
            PackageUrl = $"pkg:generic/{stored}@{Uri.EscapeDataString(version)}",
            SourcePath = kind == ProjectInventoryItemKind.Runtime ? "package.json" : "Dockerfile",
            Kind = kind
        };
        db.VulnerabilityAdvisories.Add(advisory);
        db.ProjectInventoryItems.Add(item);
        await db.SaveChangesAsync(Ct);
        var match = new VulnerabilityMatch
        {
            OrganizationId = seed.OrganizationId,
            ProjectId = seed.ProjectId,
            AdvisoryId = advisory.Id,
            ProjectInventoryItemId = item.Id,
            Ecosystem = ecosystem,
            PackageName = stored,
            Version = version,
            Status = VulnerabilityMatchStatus.Affected,
            FirstMatchedAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc)
        };
        db.VulnerabilityMatches.Add(match);
        await db.SaveChangesAsync(Ct);
        return match.Id;
    }

    /// <summary>The network, replaced: it answers nothing useful and counts every request, which must be none.</summary>
    private sealed class NetworkDouble : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
