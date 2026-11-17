// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-14: what the screens of the CVE watch read for one project (<c>GET api/analysis/cve-watch/projects/{id}</c>) and for one finding
/// (<c>GET api/analysis/cve-watch/by-finding/{id}</c>), through the real HTTP pipeline, JWT and PostgreSQL. The point is who sees what: a
/// project or a finding of another organization, or of a project the caller may not read, is a 404 with nothing of it in the answer. The only
/// double is the network of the package registries (answers 404: the recommendation of the detail then stays the advisory's).
/// </summary>
public sealed class CveWatchProjectHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed record Seeded(int OrganizationId, int First, int Second);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ProjectUrl(int projectId) => $"/api/analysis/cve-watch/projects/{projectId}";

    [Fact]
    public async Task TheProjectRead_CountsItsOwnOpenCve_ListsItsInventories_AndDatesTheLastWatchPass()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedOrganizationAsync(factory, "a");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var critical = await AddAdvisoryAsync(factory, $"GHSA-c-{tag}", AnalysisSeverity.Critical, exploited: true);
        var high = await AddAdvisoryAsync(factory, $"GHSA-h-{tag}", AnalysisSeverity.High);
        var medium = await AddAdvisoryAsync(factory, $"GHSA-m-{tag}", AnalysisSeverity.Medium);
        await AddMatchAsync(factory, a, a.First, critical, "lib-c");
        await AddMatchAsync(factory, a, a.First, high, "lib-h", fixedVersion: null);
        await AddMatchAsync(factory, a, a.First, medium, "lib-m", closed: true);
        await AddMatchAsync(factory, a, a.Second, critical, "lib-c");
        await AddInventoryAsync(factory, a, a.First, ProjectInventoryOrigin.Sbom, ProjectInventoryCoverage.Complete);
        await AddInventoryAsync(factory, a, a.First, ProjectInventoryOrigin.Repository, ProjectInventoryCoverage.Partial, "no-lock-file:npm=2", "develop");
        var watchPass = new DateTime(2026, 11, 3, 8, 17, 0, DateTimeKind.Utc);
        var watchReportId = await AddReportAsync(factory, a, a.First, CveWatchFindings.ScannerKey, watchPass);
        await AddReportAsync(factory, a, a.First, "sonarqube", watchPass.AddHours(5));
        var staleKey = $"osv:it-project-{tag}";
        await AddFeedStateAsync(factory, staleKey, DateTime.UtcNow.AddDays(-5), "secret internal failure detail");
        var allOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cvep-all-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], a.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, allOfA.Username);

        var response = await client.GetAsync(ProjectUrl(a.First), Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var project = await response.Content.ReadFromJsonAsync<CveWatchProjectDto>(IntegrationJsonOptions.Default, Ct);

        Assert.NotNull(project);
        Assert.Equal(a.First, project.ProjectId);
        // Open only (the closed one is out), distinct CVE, this project only (the CVE of Second is not counted).
        Assert.Equal(2, project.OpenCount);
        Assert.Equal(1, project.CriticalCount);
        Assert.Equal(1, project.HighCount);
        Assert.Equal(0, project.MediumCount);
        Assert.Equal(0, project.LowCount);
        Assert.Equal(1, project.ExploitedCount);
        Assert.Equal(1, project.WithoutFixCount);
        // The inventories, by origin: the repository first, with its gap codes for the page to translate.
        Assert.Equal([ProjectInventoryOrigin.Repository, ProjectInventoryOrigin.Sbom], project.Inventories.Select(inventory => inventory.Origin));
        var repository = project.Inventories[0];
        Assert.Equal(ProjectInventoryCoverage.Partial, repository.Coverage);
        Assert.Equal("no-lock-file:npm=2", repository.Reason);
        Assert.Equal("develop", repository.Ref);
        Assert.Equal(ProjectInventoryCoverage.Complete, project.Inventories[1].Coverage);
        // The last pass is the last report of the watch, not a later report of a pipeline's scanner.
        Assert.Equal(watchPass, project.LastWatchedAt);
        Assert.Equal(watchReportId, project.LastWatchRunId);
        Assert.True(project.Sources.Single(source => source.Source == staleKey).IsStale);
        // The text of a failure is the administrator's, never part of the answer.
        Assert.DoesNotContain("secret internal failure detail", await (await client.GetAsync(ProjectUrl(a.First), Ct)).Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A project the watch never passed over has no pass date, and no inventory read.
        var second = await client.GetFromJsonAsync<CveWatchProjectDto>(ProjectUrl(a.Second), IntegrationJsonOptions.Default, Ct);
        Assert.NotNull(second);
        Assert.Equal(1, second.OpenCount);
        Assert.Null(second.LastWatchedAt);
        Assert.Empty(second.Inventories);
    }

    [Fact]
    public async Task TheProjectRead_IsA404_ForAProjectOfAnotherOrganization_OrOneTheCallerMayNotRead()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedOrganizationAsync(factory, "a");
        var b = await SeedOrganizationAsync(factory, "b");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var advisory = await AddAdvisoryAsync(factory, $"GHSA-x-{tag}", AnalysisSeverity.Critical);
        await AddMatchAsync(factory, a, a.First, advisory, "lib-x");
        await AddMatchAsync(factory, b, b.First, advisory, "lib-x");
        await AddInventoryAsync(factory, b, b.First, ProjectInventoryOrigin.Sbom, ProjectInventoryCoverage.Complete);
        var allOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cvep-a-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], a.OrganizationId);
        var onlySecondOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cvep-second-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, a.Second, Permission.Read)], a.OrganizationId);
        var noneOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cvep-none-{tag}", [], a.OrganizationId);
        // Names a project of B in its grant while belonging to A: both the organization and the project scope apply.
        var namesB = await IntegrationUsers.CreateAsync(factory.Services, $"cvep-namesb-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, b.First, Permission.Read)], a.OrganizationId);
        var allOfB = await IntegrationUsers.CreateAsync(factory.Services, $"cvep-b-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], b.OrganizationId);
        using var allClient = await IntegrationUsers.LoginClientAsync(factory, allOfA.Username);
        using var secondClient = await IntegrationUsers.LoginClientAsync(factory, onlySecondOfA.Username);
        using var noneClient = await IntegrationUsers.LoginClientAsync(factory, noneOfA.Username);
        using var namesBClient = await IntegrationUsers.LoginClientAsync(factory, namesB.Username);
        using var bClient = await IntegrationUsers.LoginClientAsync(factory, allOfB.Username);

        // The positive control: the readers of the project see it.
        Assert.Equal(HttpStatusCode.OK, (await allClient.GetAsync(ProjectUrl(a.First), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bClient.GetAsync(ProjectUrl(b.First), Ct)).StatusCode);

        // Another organization, a project outside the read scope, no scope at all: not found, nothing of the project in the answer.
        foreach (var (client, projectId) in new[]
        {
            (secondClient, a.First), (noneClient, a.First), (allClient, b.First), (bClient, a.First), (namesBClient, b.First)
        })
        {
            var denied = await client.GetAsync(ProjectUrl(projectId), Ct);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.DoesNotContain("lib-x", await denied.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await allClient.GetAsync(ProjectUrl(2_000_000_000), Ct)).StatusCode);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var adminView = await admin.GetFromJsonAsync<CveWatchProjectDto>(ProjectUrl(b.First), IntegrationJsonOptions.Default, Ct);
        Assert.Equal(1, adminView!.OpenCount);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(ProjectUrl(a.First), Ct)).StatusCode);
    }

    [Fact]
    public async Task TheMatchOfAFinding_IsReadByWhoMayReadItsProject_AndByNoOneElse()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("package-feeds").ConfigurePrimaryHttpMessageHandler(() => new NoRegistry())));
        var a = await SeedOrganizationAsync(factory, "a");
        var b = await SeedOrganizationAsync(factory, "b");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var advisory = await AddAdvisoryAsync(factory, $"GHSA-f-{tag}", AnalysisSeverity.High);
        var itemId = await AddInventoryItemAsync(factory, a, a.First, "lib-find");
        await AddMatchAsync(factory, a, a.First, advisory, "lib-find", itemId: itemId);
        await PublishAsync(factory, a.First);
        var findingId = await FindingOfAsync(factory, a.First, "lib-find");
        var allOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cvef-all-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], a.OrganizationId);
        var onlySecondOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cvef-second-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, a.Second, Permission.Read)], a.OrganizationId);
        var allOfB = await IntegrationUsers.CreateAsync(factory.Services, $"cvef-b-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], b.OrganizationId);
        using var allClient = await IntegrationUsers.LoginClientAsync(host, allOfA.Username);
        using var secondClient = await IntegrationUsers.LoginClientAsync(host, onlySecondOfA.Username);
        using var bClient = await IntegrationUsers.LoginClientAsync(host, allOfB.Username);
        var url = $"/api/analysis/cve-watch/by-finding/{findingId}";

        var response = await allClient.GetAsync(url, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var match = await response.Content.ReadFromJsonAsync<CveWatchMatchDto>(IntegrationJsonOptions.Default, Ct);
        Assert.NotNull(match);
        Assert.Equal(findingId, match.FindingId);
        Assert.Equal("lib-find", match.PackageName);
        Assert.Equal(a.First, match.ProjectId);
        Assert.NotNull(match.Recommendation);

        foreach (var denied in new[] { secondClient, bClient })
            Assert.Equal(HttpStatusCode.NotFound, (await denied.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await allClient.GetAsync("/api/analysis/cve-watch/by-finding/2000000000", Ct)).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url, Ct)).StatusCode);
    }

    /// <summary>The network of the package registries, replaced: every registry answers 404.</summary>
    private sealed class NoRegistry : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) });
    }

    /// <summary>An organization with two projects, named after the label: a-first, a-second.</summary>
    private static async Task<Seeded> SeedOrganizationAsync(AetheusWebApplicationFactory factory, string label)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"cve-project-{label}-{Guid.NewGuid():N}", Slug = $"o{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var projects = new[] { "first", "second" }.Select(name => new Project { Name = $"{label}-{name}", OrganizationId = organization.Id }).ToArray();
        db.Projects.AddRange(projects);
        await db.SaveChangesAsync(Ct);
        return new Seeded(organization.Id, projects[0].Id, projects[1].Id);
    }

    private static async Task<string> AddAdvisoryAsync(AetheusWebApplicationFactory factory, string externalId, AnalysisSeverity severity, bool exploited = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.VulnerabilityAdvisories.Add(new VulnerabilityAdvisory
        {
            Source = "GHSA",
            ExternalId = externalId,
            PrimaryCveId = $"CVE-{externalId}",
            Severity = severity,
            SeverityPublished = true,
            Summary = $"Summary of {externalId}",
            ModifiedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            KevAddedAt = exploited ? new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc) : null
        });
        await db.SaveChangesAsync(Ct);
        return externalId;
    }

    private static async Task AddMatchAsync(
        AetheusWebApplicationFactory factory, Seeded organization, int projectId, string advisoryExternalId, string package,
        string? fixedVersion = "2.0.0", bool closed = false, int? itemId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var advisory = await db.VulnerabilityAdvisories.SingleAsync(row => row.ExternalId == advisoryExternalId, Ct);
        db.VulnerabilityMatches.Add(new VulnerabilityMatch
        {
            OrganizationId = organization.OrganizationId,
            ProjectId = projectId,
            AdvisoryId = advisory.Id,
            ProjectInventoryItemId = itemId,
            Ecosystem = "NuGet",
            PackageName = package,
            Version = "1.0.0",
            FixedVersion = fixedVersion,
            Status = VulnerabilityMatchStatus.Affected,
            FirstMatchedAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
            ClosedAt = closed ? new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc) : null
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task AddInventoryAsync(
        AetheusWebApplicationFactory factory, Seeded organization, int projectId, ProjectInventoryOrigin origin, ProjectInventoryCoverage coverage,
        string? reason = null, string? gitRef = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ProjectInventories.Add(NewInventory(organization, projectId, origin, coverage, reason, gitRef));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<int> AddInventoryItemAsync(AetheusWebApplicationFactory factory, Seeded organization, int projectId, string package)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inventory = NewInventory(organization, projectId, ProjectInventoryOrigin.Sbom, ProjectInventoryCoverage.Complete, null, null);
        db.ProjectInventories.Add(inventory);
        await db.SaveChangesAsync(Ct);
        var item = new ProjectInventoryItem
        {
            OrganizationId = organization.OrganizationId,
            ProjectId = projectId,
            ProjectInventoryId = inventory.Id,
            Ecosystem = "NuGet",
            PackageName = package,
            Version = "1.0.0",
            PackageUrl = $"pkg:NuGet/{package}@1.0.0",
            Kind = ProjectInventoryItemKind.Dependency
        };
        db.ProjectInventoryItems.Add(item);
        await db.SaveChangesAsync(Ct);
        return item.Id;
    }

    private static ProjectInventory NewInventory(
        Seeded organization, int projectId, ProjectInventoryOrigin origin, ProjectInventoryCoverage coverage, string? reason, string? gitRef) => new()
        {
            OrganizationId = organization.OrganizationId,
            ProjectId = projectId,
            Origin = origin,
            Coverage = coverage,
            Reason = reason,
            Ref = gitRef,
            ItemCount = 3,
            ReadAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            ChangedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc)
        };

    private static async Task AddFeedStateAsync(AetheusWebApplicationFactory factory, string source, DateTime lastSuccess, string? lastError)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.VulnerabilityFeedStates.Add(new VulnerabilityFeedState
        {
            Source = source,
            LastSuccessAt = lastSuccess,
            LastAttemptAt = lastSuccess,
            LastErrorAt = lastError is null ? null : lastSuccess.AddHours(1),
            LastError = lastError
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A report of the given scanner with no run, completed at the given time, and its number.</summary>
    private static async Task<int> AddReportAsync(AetheusWebApplicationFactory factory, Seeded organization, int projectId, string scannerKey, DateTime completedAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var report = new AnalysisReport
        {
            OrganizationId = organization.OrganizationId,
            ProjectId = projectId,
            ScannerKey = scannerKey,
            ScannerName = scannerKey,
            ScannerVersion = "1",
            Category = AnalysisCategory.Dependencies,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.NativeJson,
            ContentHash = Guid.NewGuid().ToString("N"),
            StartedAt = completedAt,
            CompletedAt = completedAt
        };
        db.AnalysisReports.Add(report);
        await db.SaveChangesAsync(Ct);
        return report.Id;
    }

    /// <summary>The report the real publisher writes for the open matches of the project: it creates their findings.</summary>
    private static async Task PublishAsync(AetheusWebApplicationFactory factory, int projectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var publisher = new CveWatchReportPublisher(
            new AnalysisRepository(db), new CveWatchReportRepository(db), new DbTransactionScope(db), TimeProvider.System,
            Options.Create(new AnalysisPlatformOptions()));
        Assert.Equal(1, await publisher.PublishProjectAsync(projectId, Ct));
    }

    private static async Task<int> FindingOfAsync(AetheusWebApplicationFactory factory, int projectId, string package)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var findingId = await db.VulnerabilityMatches.AsNoTracking()
            .Where(match => match.ProjectId == projectId && match.PackageName == package)
            .Select(match => match.AnalysisFindingId).SingleAsync(Ct);
        return Assert.NotNull(findingId);
    }
}
