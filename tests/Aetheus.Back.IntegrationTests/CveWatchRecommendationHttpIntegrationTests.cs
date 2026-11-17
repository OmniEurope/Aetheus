// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The recommendation of the vulnerability watch (PLAN-004, lot 4, points 1 to 3) through the real HTTP pipeline, JWT
/// and PostgreSQL included: the list computes it from the database alone, the detail read asks the registry.
/// The only double is the network: the "package-feeds" client answers with the Packagist reply recorded for
/// monolog/monolog on 2026-11-01 (every request is counted), and the real resolver reads it. The advisories are
/// synthetic ranges over monolog's real versions: what is proved is the path, not a claim about monolog.
/// </summary>
public sealed class CveWatchRecommendationHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Package = "monolog/monolog";
    private const string RegistryUrl = "https://repo.packagist.org/p2/monolog/monolog.json";

    // Affected below 1.27.1: a fix in the major the project is on.
    private const string FixedInMajor = """[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"1.27.1"}]}]""";

    // Affected up to and including 1.27.1, no fix stated: the whole 1.x line is affected.
    private const string WholeMajor = """[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"last_affected":"1.27.1"}]}]""";

    private sealed record Seed(int OrganizationId, int ProjectId, int InventoryId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RecordedPackagist() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Recorded", "packagist-p2-monolog-monolog.json"));

    private static WebApplicationFactory<Program> Host(AetheusWebApplicationFactory factory, RegistryDouble registry) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("package-feeds").ConfigurePrimaryHttpMessageHandler(() => registry)));

    [Fact]
    public async Task TheList_RecommendsFromTheAdvisoryAlone_WithoutAnyRegistryCall_AndOnlyForOpenAffectedMatches()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var registry = new RegistryDouble(HttpStatusCode.OK, RecordedPackagist());
        await using var host = Host(factory, registry);
        var seed = await SeedProjectAsync(host);
        await AddMatchAsync(host, seed, Package, "1.27.0", FixedInMajor);
        await AddMatchAsync(host, seed, "acme/closed", "1.0.0", FixedInMajor, closed: true);
        await AddMatchAsync(host, seed, "acme/to-verify", "1.0.0", FixedInMajor, status: VulnerabilityMatchStatus.ToVerify);
        using var admin = await IntegrationUsers.AdminClientAsync(host);

        var page = await ReadPageAsync(admin, seed.OrganizationId, "&includeClosed=true&pageSize=20");

        Assert.Equal(3, page.TotalCount);
        var open = page.Items.Single(item => item.PackageName == Package);
        Assert.NotNull(open.Recommendation);
        Assert.Equal(CveRecommendationKind.BumpDependency, open.Recommendation.Kind);
        Assert.Equal("1.27.1", open.Recommendation.TargetVersion);
        Assert.False(open.Recommendation.VerifiedAgainstRegistry);
        Assert.Equal(["composer.json"], open.Recommendation.Files);
        Assert.Null(page.Items.Single(item => item.PackageName == "acme/closed").Recommendation);
        Assert.Null(page.Items.Single(item => item.PackageName == "acme/to-verify").Recommendation);
        Assert.Empty(registry.Requests);
    }

    [Fact]
    public async Task TheDetail_ChecksTheRegistry_AndRecommendsTheNewestSafeVersionOfTheSameMajor()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var registry = new RegistryDouble(HttpStatusCode.OK, RecordedPackagist());
        await using var host = Host(factory, registry);
        var seed = await SeedProjectAsync(host);
        var matchId = await AddMatchAsync(host, seed, Package, "1.27.0", FixedInMajor);
        using var admin = await IntegrationUsers.AdminClientAsync(host);

        var detail = await ReadDetailAsync(admin, matchId);

        Assert.Equal(matchId, detail.MatchId);
        Assert.NotNull(detail.Recommendation);
        Assert.Equal(CveRecommendationKind.BumpDependency, detail.Recommendation.Kind);
        Assert.Equal("1.27.1", detail.Recommendation.TargetVersion);
        Assert.True(detail.Recommendation.VerifiedAgainstRegistry);
        Assert.Equal(RegistryUrl, Assert.Single(registry.Requests));

        // A second read is served from what was just listed: the registry is not asked again.
        await ReadDetailAsync(admin, matchId);
        Assert.Single(registry.Requests);
    }

    [Fact]
    public async Task TheDetail_IsRefusedAsNotFound_ToWhoeverMayNotReadTheProject_BeforeAnyRegistryCall()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var registry = new RegistryDouble(HttpStatusCode.OK, RecordedPackagist());
        await using var host = Host(factory, registry);
        var seed = await SeedProjectAsync(host);
        var other = await SeedProjectAsync(host);
        var matchId = await AddMatchAsync(host, seed, Package, "1.27.0", FixedInMajor);
        var reader = await IntegrationUsers.CreateAsync(host.Services, "rec-reader",
            [new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Read)], seed.OrganizationId);
        var noGrant = await IntegrationUsers.CreateAsync(host.Services, "rec-none", [], seed.OrganizationId);
        var otherOrganization = await IntegrationUsers.CreateAsync(host.Services, "rec-other",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], other.OrganizationId);
        using var readerClient = await IntegrationUsers.LoginClientAsync(host, reader.Username);
        using var noGrantClient = await IntegrationUsers.LoginClientAsync(host, noGrant.Username);
        using var otherClient = await IntegrationUsers.LoginClientAsync(host, otherOrganization.Username);
        using var anonymous = host.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await noGrantClient.GetAsync(DetailUrl(matchId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync(DetailUrl(matchId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(DetailUrl(matchId), Ct)).StatusCode);
        Assert.Empty(registry.Requests);

        // The positive control: the reader of that very project gets it, and only then is the registry asked.
        var detail = await ReadDetailAsync(readerClient, matchId);
        Assert.Equal(matchId, detail.MatchId);
        Assert.Single(registry.Requests);

        using var admin = await IntegrationUsers.AdminClientAsync(host);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(DetailUrl(matchId + 100000), Ct)).StatusCode);
    }

    private static string DetailUrl(long matchId) => $"/api/analysis/cve-watch/{matchId}";

    private static async Task<CveWatchMatchDto> ReadDetailAsync(HttpClient client, long matchId)
    {
        var response = await client.GetAsync(DetailUrl(matchId), Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<CveWatchMatchDto>(IntegrationJsonOptions.Default, Ct)
            ?? throw new InvalidOperationException("The detail came back empty.");
    }

    private static async Task<PaginatedResult<CveWatchMatchDto>> ReadPageAsync(HttpClient client, int organizationId, string query)
    {
        var response = await client.GetAsync($"/api/analysis/cve-watch?organizationId={organizationId}{query}", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<PaginatedResult<CveWatchMatchDto>>(IntegrationJsonOptions.Default, Ct)
            ?? throw new InvalidOperationException("The page came back empty.");
    }

    private static async Task<Seed> SeedProjectAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"rec-{Guid.NewGuid():N}", Slug = $"r{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = $"recommendations-{Guid.NewGuid():N}", OrganizationId = organization.Id };
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

    /// <summary>An advisory with the ranges it states for the package, the inventory item that holds it, and the match.</summary>
    private static async Task<long> AddMatchAsync(
        WebApplicationFactory<Program> factory, Seed seed, string package, string version, string ranges,
        bool closed = false, VulnerabilityMatchStatus status = VulnerabilityMatchStatus.Affected)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var advisory = new VulnerabilityAdvisory
        {
            Source = "GHSA",
            ExternalId = $"GHSA-{Guid.NewGuid():N}"[..24],
            PrimaryCveId = "CVE-2099-0042",
            Severity = AnalysisSeverity.High,
            SeverityPublished = true,
            ModifiedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            AffectedPackages = [new VulnerabilityAffectedPackage { Ecosystem = "Packagist", PackageName = package, RangesJson = ranges }]
        };
        var item = new ProjectInventoryItem
        {
            OrganizationId = seed.OrganizationId,
            ProjectId = seed.ProjectId,
            ProjectInventoryId = seed.InventoryId,
            Ecosystem = "Packagist",
            PackageName = package,
            Version = version,
            PackageUrl = $"pkg:composer/{package}@{version}",
            SourcePath = "composer.json",
            Kind = ProjectInventoryItemKind.Dependency
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
            Ecosystem = "Packagist",
            PackageName = package,
            Version = version,
            Status = status,
            FirstMatchedAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
            ClosedAt = closed ? new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc) : null
        };
        db.VulnerabilityMatches.Add(match);
        await db.SaveChangesAsync(Ct);
        return match.Id;
    }

    /// <summary>The network, replaced: one recorded answer for every request, each request counted.</summary>
    private sealed class RegistryDouble(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
