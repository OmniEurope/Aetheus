// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-10, one more SQL lot: the release and CVE lists. Each runs through its real repository on a real
/// PostgreSQL and the SQL commands it sends are counted (and kept, so a failing assertion names the statements).
/// <list type="bullet">
/// <item>A list read as a count, then a page, scanned the filtered set twice; a page shorter than its size already
/// holds the total, so only a full page (or an empty page past the end) pays the count.</item>
/// <item>A page cut from a sort key that has ties is not stable unless the row's id closes the order.</item>
/// <item>The release the redeploy offer names is picked by the database, one row per project.</item>
/// </list>
/// </summary>
public sealed class ListPageCommandsIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Published = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private static bool IsCount(string statement) => statement.Contains("count(", StringComparison.OrdinalIgnoreCase);

    private async Task<(int OrganizationId, int ProjectId)> SeedProjectAsync(string label)
    {
        await using var db = NewContext();
        var organizationId = await db.Organizations.OrderBy(item => item.Id).Select(item => item.Id).FirstAsync(Ct);
        var project = new Project { Name = $"{label}-{Guid.NewGuid():N}", OrganizationId = organizationId, DefaultBranch = "main" };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        return (organizationId, project.Id);
    }

    private async Task<List<int>> SeedReleasesAsync(int projectId, int count, ReleaseStatus status = ReleaseStatus.Published)
    {
        await using var db = NewContext();
        var releases = Enumerable.Range(1, count).Select(index => new Release
        {
            ProjectId = projectId,
            Version = $"1.0.{index}",
            BranchName = $"release/1.0.{index}",
            Status = status,
            DetectedAt = Published,
            // Every release shares the date: the sort keys below all have ties.
            PublishedAt = Published,
            BuildNumber = index
        }).ToList();
        db.Releases.AddRange(releases);
        await db.SaveChangesAsync(Ct);
        return [.. releases.Select(release => release.Id)];
    }

    private static async Task<SqlCommandCounter.Measure<(List<Release> Items, int TotalCount)>> MeasureReleasePageAsync(
        SqlCommandCounter counter, AppDbContext db, int projectId, int page, int pageSize, string? sortBy = null) =>
        await counter.MeasureAsync(() => new ReleaseRepository(db)
            .GetReleasesPagedAsync(null, projectId, page, pageSize, null, Ct, sortBy));

    [Theory]
    // The whole list fits the page: the page is the total, no count.
    [InlineData(1, 25, 5, 5, false)]
    // A last page that is not full says how many rows the earlier pages held.
    [InlineData(2, 3, 2, 5, false)]
    // A full page cannot say whether rows follow it: the filtered set is counted.
    [InlineData(1, 3, 3, 5, true)]
    [InlineData(1, 5, 5, 5, true)]
    // An empty page past the end tells nothing either.
    [InlineData(4, 3, 0, 5, true)]
    public async Task ReleaseList_CountsTheFilteredSetOnlyWhenThePageCannotSayTheTotal(
        int page, int pageSize, int expectedItems, int expectedTotal, bool counted)
    {
        var (_, projectId) = await SeedProjectAsync("release-list");
        await SeedReleasesAsync(projectId, 5);
        await using var db = NewContext();
        using var counter = new SqlCommandCounter();

        var measure = await MeasureReleasePageAsync(counter, db, projectId, page, pageSize);

        Assert.Equal(expectedItems, measure.Result.Items.Count);
        Assert.Equal(expectedTotal, measure.Result.TotalCount);
        Assert.Equal(counted, measure.Statements.Any(IsCount));
        Assert.True(counted || measure.Commands <= 2,
            $"The last page sent {measure.Commands} SQL commands (the page and its artifacts, no count):{System.Environment.NewLine}"
            + string.Join(System.Environment.NewLine, measure.Statements));
    }

    [Fact]
    public async Task ReleaseList_PagesTheSameSetThroughATiedSortWithoutRepeatingOrLosingARow()
    {
        var (_, projectId) = await SeedProjectAsync("release-ties");
        var ids = await SeedReleasesAsync(projectId, 7);
        await using var db = NewContext();
        using var counter = new SqlCommandCounter();

        var seen = new List<int>();
        SqlCommandCounter.Measure<(List<Release> Items, int TotalCount)>? first = null;
        for (var page = 1; page <= 3; page++)
        {
            var measure = await MeasureReleasePageAsync(counter, db, projectId, page, 3, sortBy: "status");
            first ??= measure;
            seen.AddRange(measure.Result.Items.Select(release => release.Id));
        }

        Assert.Equal(ids.Order(), seen.Order());
        Assert.Equal(7, seen.Distinct().Count());
        // The page statement: the user's key, then the id, so the order is total.
        Assert.Matches(new Regex("ORDER BY .*\"Status\" DESC, .*\"Id\" DESC", RegexOptions.Singleline),
            first!.Statements.First(statement => statement.Contains("LIMIT", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ServerReleaseList_ReadsAShortPageWithoutCountingTheServersProjectsAgain()
    {
        var (organizationId, projectId) = await SeedProjectAsync("server-releases");
        await SeedReleasesAsync(projectId, 2);
        int serverId;
        await using (var seed = NewContext())
        {
            var server = new Server
            {
                Name = $"releases-host-{Guid.NewGuid():N}",
                Hostname = $"releases-{Guid.NewGuid():N}",
                Status = ServerStatus.Online,
                OrganizationId = organizationId
            };
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(Ct);
            seed.ProjectServers.Add(new ProjectServer { ProjectId = projectId, ServerId = server.Id, DisplayName = "host" });
            await seed.SaveChangesAsync(Ct);
            serverId = server.Id;
        }
        await using var db = NewContext();
        using var counter = new SqlCommandCounter();

        var measure = await counter.MeasureAsync(() => new ReleaseRepository(db)
            .GetReleasesForServerPagedAsync(serverId, 1, 25, Ct));

        Assert.Equal(2, measure.Result.Items.Count);
        Assert.Equal(2, measure.Result.TotalCount);
        Assert.False(measure.Statements.Any(IsCount), string.Join(System.Environment.NewLine, measure.Statements));
        Assert.Equal(1, measure.Commands);
    }

    [Fact]
    public async Task RedeployTargets_TakeTheNewestSupersededReleaseOfEachProject_PickedByTheDatabase()
    {
        var (_, projectId) = await SeedProjectAsync("redeploy-targets");
        var ids = await SeedReleasesAsync(projectId, 4, ReleaseStatus.Superseded);
        await using (var seed = NewContext())
        {
            // The third one was superseded the latest: it is the release production ran just before the live one.
            var releases = await seed.Releases.Where(release => ids.Contains(release.Id)).OrderBy(release => release.Id).ToListAsync(Ct);
            for (var index = 0; index < releases.Count; index++)
                releases[index].PublishedAt = Published.AddDays(index == 2 ? 10 : index);
            await seed.SaveChangesAsync(Ct);
        }
        await using var db = NewContext();
        using var counter = new SqlCommandCounter();

        var measure = await counter.MeasureAsync(() => new ReleaseRepository(db).GetRedeployTargetsAsync([projectId], Ct));

        Assert.Equal(ids.Order().ElementAt(2), measure.Result[projectId].PreviousReleaseId);
        // One row per project leaves the database, not the whole superseded history.
        Assert.Contains("LIMIT 1", measure.Statements[0], StringComparison.Ordinal);
    }

    private async Task<int> SeedAdvisoryAsync(AnalysisSeverity severity = AnalysisSeverity.High)
    {
        await using var db = NewContext();
        var advisory = new VulnerabilityAdvisory
        {
            Source = "GHSA",
            ExternalId = $"GHSA-{Guid.NewGuid():N}",
            Severity = severity,
            ModifiedAt = Published
        };
        db.VulnerabilityAdvisories.Add(advisory);
        await db.SaveChangesAsync(Ct);
        return advisory.Id;
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(3, true)]
    public async Task CveWatchPage_CountsTheMatchesOnlyForAFullPage(int pageSize, bool counted)
    {
        var (organizationId, projectId) = await SeedProjectAsync("cve-watch");
        await using (var seed = NewContext())
        {
            foreach (var index in Enumerable.Range(1, 3))
                seed.VulnerabilityMatches.Add(new VulnerabilityMatch
                {
                    OrganizationId = organizationId,
                    ProjectId = projectId,
                    AdvisoryId = await SeedAdvisoryAsync(),
                    Ecosystem = "NuGet",
                    PackageName = $"pkg-{index}",
                    Version = "1.0.0",
                    Status = VulnerabilityMatchStatus.Affected,
                    FirstMatchedAt = Published
                });
            await seed.SaveChangesAsync(Ct);
        }
        await using var db = NewContext();
        using var counter = new SqlCommandCounter();

        var measure = await counter.MeasureAsync(() => new CveWatchQueryRepository(db)
            .GetPageAsync(organizationId, null, projectId, includeClosed: false, 1, pageSize, Ct));

        Assert.Equal(3, measure.Result.Items.Count);
        Assert.Equal(3, measure.Result.TotalCount);
        Assert.Equal(counted, measure.Statements.Any(IsCount));
        Assert.Equal(counted ? 2 : 1, measure.Commands);
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(2, true)]
    public async Task ServerHostFindings_CountTheFindingsOnlyForAFullPage(int pageSize, bool counted)
    {
        var (organizationId, _) = await SeedProjectAsync("host-findings");
        int serverId;
        await using (var seed = NewContext())
        {
            var server = new Server
            {
                Name = $"findings-host-{Guid.NewGuid():N}",
                Hostname = $"findings-{Guid.NewGuid():N}",
                Status = ServerStatus.Online,
                OrganizationId = organizationId
            };
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(Ct);
            serverId = server.Id;
            foreach (var index in Enumerable.Range(1, 2))
                seed.ServerVulnerabilityFindings.Add(new ServerVulnerabilityFinding
                {
                    ServerId = serverId,
                    AdvisoryId = await SeedAdvisoryAsync(),
                    Ecosystem = "Debian:12",
                    PackageName = $"openssl-{index}",
                    InstalledVersion = "3.0.0",
                    Status = VulnerabilityMatchStatus.Affected,
                    FirstMatchedAt = Published
                });
            await seed.SaveChangesAsync(Ct);
        }
        await using var db = NewContext();
        using var counter = new SqlCommandCounter();

        var measure = await counter.MeasureAsync(() => new ServerHostVulnerabilityRepository(db).GetPageAsync(
            new ServerHostFindingScope(null, [serverId]),
            new ServerHostFindingFilter(null, null, null, false, false), 1, pageSize, Ct));

        Assert.Equal(2, measure.Result.Items.Count);
        Assert.Equal(2, measure.Result.TotalCount);
        Assert.Equal(counted, measure.Statements.Any(IsCount));
        Assert.Equal(counted ? 2 : 1, measure.Commands);
    }

    [Fact]
    public async Task VulnerabilityObservations_PageThroughASnapshotWhoseRowsShareTheirDate_WithoutACountOnTheLastPage()
    {
        var (organizationId, projectId) = await SeedProjectAsync("observations");
        List<int> ids;
        await using (var seed = NewContext())
        {
            var tracking = new AnalysisTrackingProject
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                ExternalProjectId = "ext",
                ExternalProjectName = "ext",
                CreatedAt = Published,
                UpdatedAt = Published
            };
            seed.AnalysisTrackingProjects.Add(tracking);
            await seed.SaveChangesAsync(Ct);
            var rows = Enumerable.Range(1, 5).Select(index => new AnalysisVulnerabilityObservation
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                AnalysisTrackingProjectId = tracking.Id,
                VulnerabilityId = $"CVE-2026-{index:D4}",
                ComponentName = "lib",
                ComponentVersion = "1.0.0",
                Severity = AnalysisSeverity.High,
                Status = "Open",
                ObservedAt = Published,
                // One of them has no package URL: the search must still run.
                PackageUrl = index == 1 ? null : $"pkg:nuget/lib@{index}"
            }).ToList();
            seed.AnalysisVulnerabilityObservations.AddRange(rows);
            await seed.SaveChangesAsync(Ct);
            ids = [.. rows.Select(row => row.Id)];
        }
        await using var db = NewContext();
        using var counter = new SqlCommandCounter();
        var repository = new AnalysisInsightsRepository(db);

        var seen = new List<int>();
        var statements = new List<string>();
        var commands = 0;
        foreach (var page in new[] { 1, 2, 3 })
        {
            var measure = await counter.MeasureAsync(() => repository.GetVulnerabilityObservationsAsync(
                projectId, new PaginationRequest { Page = page, PageSize = 2 }, Ct));
            seen.AddRange(measure.Result.Items.Select(item => item.Id));
            Assert.Equal(5, measure.Result.TotalCount);
            statements.AddRange(measure.Statements);
            commands += measure.Commands;
        }

        // Equal dates, equal severities: without the id as last key the three pages were free to overlap.
        Assert.Equal(ids.Order(), seen.Order());
        // Pages 1 and 2 are full (counted), page 3 holds the last row and is not.
        Assert.Equal(2, statements.Count(IsCount));
        Assert.Equal(5, commands);
        Assert.Matches(new Regex("ORDER BY .*\"ObservedAt\" DESC, .*\"Id\" DESC", RegexOptions.Singleline),
            statements.First(statement => statement.Contains("LIMIT", StringComparison.Ordinal)));

        var searched = await repository.GetVulnerabilityObservationsAsync(
            projectId, new PaginationRequest { Search = "pkg:nuget" }, Ct);
        Assert.Equal(4, searched.TotalCount);
    }
}
