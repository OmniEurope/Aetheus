// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The CVE watch tab of the quality page (R-15) and the page of one watch run (R-22) through the real HTTP pipeline, JWT
/// and PostgreSQL: the advisories grouped across organizations, the tile figures, the freshness of the sources and the run
/// of a report the real publisher wrote. The point is who sees what: a project the caller may not read never shows up in a
/// row, a count, a run or the list of uncovered projects.
/// </summary>
public sealed class CveWatchOverviewHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Advisories = "/api/analysis/cve-watch/advisories";
    private const string Overview = "/api/analysis/cve-watch/overview";

    private sealed record Seeded(int OrganizationId, int First, int Second, int Third);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAdvisories_AreGroupedByCve_AcrossOrganizations_AndOnlyHoldWhatTheCallerMayRead()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedOrganizationAsync(factory, "a");
        var b = await SeedOrganizationAsync(factory, "b");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var exploited = await AddAdvisoryAsync(factory, $"GHSA-x-{tag}", AnalysisSeverity.Critical, exploited: true);
        var noFix = await AddAdvisoryAsync(factory, $"GHSA-y-{tag}", AnalysisSeverity.Medium);
        var closed = await AddAdvisoryAsync(factory, $"GHSA-z-{tag}", AnalysisSeverity.High);
        await AddMatchAsync(factory, a, a.First, exploited, "lib-x");
        await AddMatchAsync(factory, a, a.Second, exploited, "lib-x");
        await AddMatchAsync(factory, b, b.First, exploited, "lib-x");
        await AddMatchAsync(factory, a, a.First, noFix, "lib-y", fixedVersion: null);
        await AddMatchAsync(factory, a, a.Second, closed, "lib-z", closed: true);
        var allOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cve-all-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], a.OrganizationId);
        var onlyFirstOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cve-one-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, a.First, Permission.Read)], a.OrganizationId);
        var noneOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cve-none-{tag}", [], a.OrganizationId);
        var namesB = await IntegrationUsers.CreateAsync(factory.Services, $"cve-namesb-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, b.First, Permission.Read)], a.OrganizationId);
        using var allClient = await IntegrationUsers.LoginClientAsync(factory, allOfA.Username);
        using var oneClient = await IntegrationUsers.LoginClientAsync(factory, onlyFirstOfA.Username);
        using var noneClient = await IntegrationUsers.LoginClientAsync(factory, noneOfA.Username);
        using var namesBClient = await IntegrationUsers.LoginClientAsync(factory, namesB.Username);

        // The positive control: a reader of every project of A sees the exploited CVE first, then the one with no fix, with its two projects.
        var everything = await ReadAsync(allClient, $"?pageSize=100&organizationId={a.OrganizationId}");
        Assert.Equal([exploited, noFix], everything.Items.Select(group => group.AdvisoryId));
        Assert.Equal(["a-first", "a-second"], everything.Items[0].Matches.Select(match => match.ProjectName));
        Assert.Equal(AnalysisSeverity.Critical, everything.Items[0].Severity);
        Assert.NotNull(everything.Items[0].ExploitedSince);
        Assert.All(everything.Items.SelectMany(group => group.Matches), match => Assert.Equal(a.OrganizationId, match.OrganizationId));
        Assert.Equal(2, everything.TotalCount);

        // Without naming an organization, the caller's own are all it gets: still nothing of B.
        var unscoped = await ReadAsync(allClient, "?pageSize=100");
        Assert.DoesNotContain(unscoped.Items.SelectMany(group => group.Matches), match => match.OrganizationId == b.OrganizationId);

        // Closed matches only come when asked for.
        var withClosed = await ReadAsync(allClient, "?pageSize=100&includeClosed=true");
        Assert.Contains(closed, withClosed.Items.Select(group => group.AdvisoryId));
        Assert.DoesNotContain(closed, unscoped.Items.Select(group => group.AdvisoryId));

        // A reader of one project sees that project's rows and not the other's.
        var limited = await ReadAsync(oneClient, "?pageSize=100");
        Assert.Equal([exploited, noFix], limited.Items.Select(group => group.AdvisoryId));
        Assert.Equal(["a-first"], limited.Items[0].Matches.Select(match => match.ProjectName));
        Assert.Empty((await ReadAsync(noneClient, "?pageSize=100")).Items);
        Assert.Empty((await ReadAsync(namesBClient, "?pageSize=100")).Items);

        // Another organization is refused outright, with nothing of it in the answer.
        var foreign = await allClient.GetAsync($"{Advisories}?organizationId={b.OrganizationId}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.DoesNotContain("b-first", await foreign.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A site Admin reads every organization.
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var adminPage = await ReadAsync(admin, $"?pageSize=100&organizationId={b.OrganizationId}");
        Assert.Equal(["b-first"], Assert.Single(adminPage.Items).Matches.Select(match => match.ProjectName));
    }

    [Fact]
    public async Task TheAdvisoryFilters_AndThePaging_NarrowTheList()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedOrganizationAsync(factory, "a");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var exploited = await AddAdvisoryAsync(factory, $"GHSA-x-{tag}", AnalysisSeverity.Critical, exploited: true);
        var noFix = await AddAdvisoryAsync(factory, $"GHSA-y-{tag}", AnalysisSeverity.Medium);
        await AddMatchAsync(factory, a, a.First, exploited, "lib-x");
        await AddMatchAsync(factory, a, a.Second, exploited, "lib-x");
        await AddMatchAsync(factory, a, a.First, noFix, "lib-y", fixedVersion: null);
        var user = await IntegrationUsers.CreateAsync(factory.Services, $"cve-filter-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], a.OrganizationId);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        Assert.Equal([exploited], (await ReadAsync(client, "?severity=Critical")).Items.Select(group => group.AdvisoryId));
        Assert.Equal([exploited], (await ReadAsync(client, "?exploitedOnly=true")).Items.Select(group => group.AdvisoryId));
        Assert.Equal([noFix], (await ReadAsync(client, "?withoutFixOnly=true")).Items.Select(group => group.AdvisoryId));
        var second = await ReadAsync(client, $"?projectId={a.Second}");
        Assert.Equal([exploited], second.Items.Select(group => group.AdvisoryId));
        Assert.Equal(["a-second"], Assert.Single(second.Items).Matches.Select(match => match.ProjectName));

        var firstPage = await ReadAsync(client, "?pageSize=1&page=1");
        var secondPage = await ReadAsync(client, "?pageSize=1&page=2");
        Assert.Equal(2, firstPage.TotalCount);
        Assert.Equal([exploited], firstPage.Items.Select(group => group.AdvisoryId));
        Assert.Equal([noFix], secondPage.Items.Select(group => group.AdvisoryId));

        foreach (var url in new[] { $"{Advisories}?page=0", $"{Advisories}?severity=Nonsense" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url, Ct)).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Advisories, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Overview, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheOverview_CountsOpenCve_ListsUncoveredProjects_AndTellsHowFreshTheSourcesAre_ForTheReadableProjectsOnly()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedOrganizationAsync(factory, "a");
        var b = await SeedOrganizationAsync(factory, "b");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var critical = await AddAdvisoryAsync(factory, $"GHSA-c-{tag}", AnalysisSeverity.Critical, exploited: true);
        var noFix = await AddAdvisoryAsync(factory, $"GHSA-n-{tag}", AnalysisSeverity.Low);
        var ofB = await AddAdvisoryAsync(factory, $"GHSA-b-{tag}", AnalysisSeverity.Critical, exploited: true);
        await AddMatchAsync(factory, a, a.First, critical, "lib-c");
        await AddMatchAsync(factory, a, a.Second, critical, "lib-c");
        await AddMatchAsync(factory, a, a.Second, noFix, "lib-n", fixedVersion: null);
        await AddMatchAsync(factory, b, b.First, ofB, "lib-b", fixedVersion: null);
        // First is fully read, Second only partly, Third never.
        await AddInventoryAsync(factory, a, a.First, ProjectInventoryCoverage.Complete);
        await AddInventoryAsync(factory, a, a.Second, ProjectInventoryCoverage.Partial);
        await AddInventoryAsync(factory, b, b.First, ProjectInventoryCoverage.None);
        var staleKey = $"osv:it-stale-{tag}";
        var freshKey = $"osv:it-fresh-{tag}";
        await AddFeedStateAsync(factory, staleKey, lastSuccess: DateTime.UtcNow.AddDays(-5), lastError: "secret internal failure detail");
        await AddFeedStateAsync(factory, freshKey, lastSuccess: DateTime.UtcNow.AddMinutes(-5), lastError: null);
        var allOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cve-ov-all-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], a.OrganizationId);
        var onlyFirstOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cve-ov-one-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, a.First, Permission.Read)], a.OrganizationId);
        using var allClient = await IntegrationUsers.LoginClientAsync(factory, allOfA.Username);
        using var oneClient = await IntegrationUsers.LoginClientAsync(factory, onlyFirstOfA.Username);

        var all = await ReadOverviewAsync(allClient);
        Assert.Equal(1, all.CriticalCount);
        Assert.Equal(1, all.ExploitedCount);
        Assert.Equal(1, all.WithoutFixCount);
        var uncovered = all.UncoveredProjects.ToDictionary(project => project.ProjectName);
        Assert.Equal(ProjectInventoryCoverage.Partial, uncovered["a-second"].Coverage);
        Assert.Equal(ProjectInventoryCoverage.None, uncovered["a-third"].Coverage);
        Assert.DoesNotContain("a-first", uncovered.Keys);
        Assert.DoesNotContain(all.UncoveredProjects, project => project.OrganizationId == b.OrganizationId);

        // The reader of one project counts that project only: the unfixed one of Second and the CVE of B are not its.
        var limited = await ReadOverviewAsync(oneClient);
        Assert.Equal(1, limited.CriticalCount);
        Assert.Equal(1, limited.ExploitedCount);
        Assert.Equal(0, limited.WithoutFixCount);
        Assert.Empty(limited.UncoveredProjects);

        var sources = all.Sources.ToDictionary(source => source.Source);
        Assert.True(sources[staleKey].IsStale);
        Assert.False(sources[freshKey].IsStale);
        Assert.NotNull(sources[staleKey].LastErrorAt);
        // The text of a failure is the administrator's, never part of the answer.
        var raw = await (await allClient.GetAsync(Overview, Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("secret internal failure detail", raw, StringComparison.Ordinal);

        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var adminView = await ReadOverviewAsync(admin);
        Assert.Contains(adminView.UncoveredProjects, project => project.ProjectName == "b-first");
    }

    [Fact]
    public async Task ARunOfTheWatch_IsReadByItsNumber_ByWhoMayReadItsProject_AndNeverAPipelineReport()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedOrganizationAsync(factory, "a");
        var b = await SeedOrganizationAsync(factory, "b");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var advisory = await AddAdvisoryAsync(factory, $"GHSA-r-{tag}", AnalysisSeverity.High);
        var itemId = await AddInventoryItemAsync(factory, a, a.First, "lib-run");
        await AddMatchAsync(factory, a, a.First, advisory, "lib-run", itemId: itemId);
        var reportId = await PublishAsync(factory, a.First);
        var pipelineReportId = await AddPipelineReportAsync(factory, a, a.First);
        var allOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cve-run-all-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], a.OrganizationId);
        var onlySecondOfA = await IntegrationUsers.CreateAsync(factory.Services, $"cve-run-second-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, a.Second, Permission.Read)], a.OrganizationId);
        var ofB = await IntegrationUsers.CreateAsync(factory.Services, $"cve-run-b-{tag}",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], b.OrganizationId);
        using var allClient = await IntegrationUsers.LoginClientAsync(factory, allOfA.Username);
        using var secondClient = await IntegrationUsers.LoginClientAsync(factory, onlySecondOfA.Username);
        using var bClient = await IntegrationUsers.LoginClientAsync(factory, ofB.Username);

        var run = await allClient.GetFromJsonAsync<CveWatchRunDto>($"/api/analysis/cve-watch/runs/{reportId}", IntegrationJsonOptions.Default, Ct);
        Assert.NotNull(run);
        Assert.Equal(reportId, run.ReportId);
        Assert.Equal("a-first", run.ProjectName);
        Assert.Equal(1, run.FindingCount);
        Assert.Equal(1, run.NewFindingCount);
        Assert.Equal(1, run.PackageCount);
        var finding = Assert.Single(run.Findings);
        Assert.True(finding.IsNew);
        Assert.Equal(["lib-run"], finding.Packages);

        // Who may not read the project, who belongs to another organization, a pipeline's report and an unknown number: all not found.
        foreach (var client in new[] { secondClient, bClient })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/analysis/cve-watch/runs/{reportId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await allClient.GetAsync($"/api/analysis/cve-watch/runs/{pipelineReportId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await allClient.GetAsync("/api/analysis/cve-watch/runs/2000000000", Ct)).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/analysis/cve-watch/runs/{reportId}", Ct)).StatusCode);

        // R-21: the Executions list names the watch as the source of that report, and the pipeline as the source of the other.
        var portfolio = await allClient.GetFromJsonAsync<PaginatedResult<AnalysisPortfolioRowDto>>(
            $"/api/analysis/portfolio?projectId={a.First}&pageSize=50", IntegrationJsonOptions.Default, Ct);
        Assert.NotNull(portfolio);
        Assert.Equal(AnalysisScanSource.CveWatch, portfolio.Items.Single(row => row.AnalysisReportId == reportId).Source);
        Assert.Equal(AnalysisScanSource.Pipeline, portfolio.Items.Single(row => row.AnalysisReportId == pipelineReportId).Source);
    }

    private static async Task<PaginatedResult<CveWatchAdvisoryGroupDto>> ReadAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(Advisories + query, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<PaginatedResult<CveWatchAdvisoryGroupDto>>(IntegrationJsonOptions.Default, Ct)
            ?? throw new InvalidOperationException("The page came back empty.");
    }

    private static async Task<CveWatchOverviewDto> ReadOverviewAsync(HttpClient client)
    {
        var response = await client.GetAsync(Overview, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<CveWatchOverviewDto>(IntegrationJsonOptions.Default, Ct)
            ?? throw new InvalidOperationException("The overview came back empty.");
    }

    /// <summary>An organization with three projects, named after the label: a-first, a-second, a-third.</summary>
    private static async Task<Seeded> SeedOrganizationAsync(AetheusWebApplicationFactory factory, string label)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"cve-overview-{label}-{Guid.NewGuid():N}", Slug = $"o{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var projects = new[] { "first", "second", "third" }.Select(name => new Project { Name = $"{label}-{name}", OrganizationId = organization.Id }).ToArray();
        db.Projects.AddRange(projects);
        await db.SaveChangesAsync(Ct);
        return new Seeded(organization.Id, projects[0].Id, projects[1].Id, projects[2].Id);
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

    private static async Task AddInventoryAsync(AetheusWebApplicationFactory factory, Seeded organization, int projectId, ProjectInventoryCoverage coverage)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ProjectInventories.Add(NewInventory(organization, projectId, coverage));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<int> AddInventoryItemAsync(AetheusWebApplicationFactory factory, Seeded organization, int projectId, string package)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inventory = NewInventory(organization, projectId, ProjectInventoryCoverage.Complete);
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

    private static ProjectInventory NewInventory(Seeded organization, int projectId, ProjectInventoryCoverage coverage) => new()
    {
        OrganizationId = organization.OrganizationId,
        ProjectId = projectId,
        Origin = ProjectInventoryOrigin.Sbom,
        Coverage = coverage,
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

    /// <summary>The report the real publisher writes for the open matches of the project, and its number.</summary>
    private static async Task<int> PublishAsync(AetheusWebApplicationFactory factory, int projectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var publisher = new CveWatchReportPublisher(
            new AnalysisRepository(db), new CveWatchReportRepository(db), new DbTransactionScope(db), TimeProvider.System,
            Options.Create(new AnalysisPlatformOptions()));
        Assert.Equal(1, await publisher.PublishProjectAsync(projectId, Ct));
        return await db.AnalysisReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId && report.ScannerKey == CveWatchFindings.ScannerKey)
            .Select(report => report.Id).SingleAsync(Ct);
    }

    /// <summary>A report that belongs to another scanner than the watch, with no run: not a watch run.</summary>
    private static async Task<int> AddPipelineReportAsync(AetheusWebApplicationFactory factory, Seeded organization, int projectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var report = new AnalysisReport
        {
            OrganizationId = organization.OrganizationId,
            ProjectId = projectId,
            ScannerKey = "sonarqube",
            ScannerName = "SonarQube",
            ScannerVersion = "1",
            Category = AnalysisCategory.Sast,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.NativeJson,
            ContentHash = Guid.NewGuid().ToString("N"),
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };
        db.AnalysisReports.Add(report);
        await db.SaveChangesAsync(Ct);
        return report.Id;
    }
}
