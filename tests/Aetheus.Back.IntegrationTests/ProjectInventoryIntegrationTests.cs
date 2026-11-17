// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Back.Tests;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The whole read on PostgreSQL over real bare Git repositories: the migration, the real Git commands, the
/// real parsers. The commits go through <see cref="GitLightCliService"/>, so receive-pack and the smart HTTP
/// route are the one part not exercised here.
/// </summary>
public sealed class ProjectInventoryIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture), IDisposable
{
    private const string Lock = """{"version":2,"dependencies":{"net10.0":{"Serilog":{"type":"Direct","resolved":"4.0.0"},"Polly":{"type":"Transitive","resolved":"8.0.0"}}}}""";

    private readonly string _root = Directory.CreateTempSubdirectory("inventory-").FullName;
    private readonly MovableClock _clock = new();
    private readonly GitLightCliService _cli = NewCli();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class MovableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ErrorLog : ILogger<ProjectInventoryService>
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null) Errors.Add(exception);
        }
    }

    public void Dispose()
    {
        // git writes read-only object files, which a recursive delete refuses on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private static GitLightCliService NewCli()
    {
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        return new GitLightCliService(
            runner, new GitLightCliWriter(runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance, TimeProvider.System);
    }

    private string DiskPath(int projectId, string slug) => Path.Combine(_root, projectId.ToString(), slug + ".git");

    private ProjectInventoryService NewService(AppDbContext db, IVulnerabilityMatchService? matching = null, ILogger<ProjectInventoryService>? logger = null)
    {
        var git = Substitute.For<IGitLightService>();
        git.ResolveDiskPath(Arg.Any<int>(), Arg.Any<string>())
            .Returns(call => DiskPath(call.ArgAt<int>(0), call.ArgAt<string>(1)));
        var options = Options.Create(new VulnerabilityWatchOptions());
        var reader = new RepositoryInventoryReader(
            new GitProjectSourceReader(new GitLightRepository(db), _cli, git), options, NullLogger<RepositoryInventoryReader>.Instance);
        matching ??= NewMatching(db);
        return new ProjectInventoryService(
            new ProjectInventoryRepository(db), reader, new ProjectInventoryGate(), matching, options, _clock,
            logger ?? NullLogger<ProjectInventoryService>.Instance);
    }

    private VulnerabilityMatchService NewMatching(AppDbContext db) =>
        new(new VulnerabilityMatchRepository(db), _clock, NullLogger<VulnerabilityMatchService>.Instance);

    private async Task RefreshAsync(int projectId)
    {
        await using var db = NewContext();
        await NewService(db).RefreshProjectAsync(projectId, Ct);
    }

    private async Task<(Organization Organization, Project Project)> SeedProjectAsync(string name)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = name, Slug = name.ToLowerInvariant(), Description = "d" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = name, Description = "d", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        return (organization, project);
    }

    private async Task<int> SeedRepositoryAsync(int projectId, string slug, bool isConfiguration = false)
    {
        var diskPath = DiskPath(projectId, slug);
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        await _cli.InitBareRepoAsync(diskPath, "main", Ct);
        await using var db = NewContext();
        var repository = new GitInternalRepo
        {
            ProjectId = projectId,
            Name = slug,
            Slug = slug,
            DefaultBranch = "main",
            IsEmpty = false,
            IsConfiguration = isConfiguration
        };
        db.GitInternalRepos.Add(repository);
        await db.SaveChangesAsync(Ct);
        return repository.Id;
    }

    private async Task<string> CommitAsync(int projectId, string slug, string path, string content)
    {
        var (success, sha, error) = await _cli.CommitFileAsync(
            DiskPath(projectId, slug), "main", path, content, "chore: change", "Test", "test@example.test", Ct);
        Assert.True(success, error);
        return sha!;
    }

    private async Task<List<ProjectInventory>> InventoriesAsync(int projectId)
    {
        await using var db = NewContext();
        return await db.ProjectInventories.AsNoTracking().Include(inventory => inventory.Items)
            .Where(inventory => inventory.ProjectId == projectId).ToListAsync(Ct);
    }

    [Fact]
    public async Task ARealCommit_ChangesTheStoredInventory_AndKeepsTheRowsOfWhatDidNotChange()
    {
        await ResetAndMigrateAsync();
        var (_, project) = await SeedProjectAsync("Alpha");
        await SeedRepositoryAsync(project.Id, "alpha");
        var first = await CommitAsync(project.Id, "alpha", "src/App/packages.lock.json", Lock);

        await RefreshAsync(project.Id);
        var before = (await InventoriesAsync(project.Id)).Single(item => item.Origin == ProjectInventoryOrigin.Repository);
        Assert.Equal(ProjectInventoryCoverage.Complete, before.Coverage);
        Assert.Equal(first, before.CommitSha);
        Assert.Equal("main", before.Ref);
        Assert.Equal(["polly@8.0.0", "serilog@4.0.0"], before.Items.Select(item => $"{item.PackageName}@{item.Version}").Order());
        var pollyId = before.Items.Single(item => item.PackageName == "polly").Id;

        // A commit that touches no manifest: same content, only the commit and the read date move.
        var second = await CommitAsync(project.Id, "alpha", "README.md", "# Hello");
        _clock.Advance(TimeSpan.FromHours(1));
        await RefreshAsync(project.Id);
        var same = (await InventoriesAsync(project.Id)).Single(item => item.Origin == ProjectInventoryOrigin.Repository);
        Assert.Equal(second, same.CommitSha);
        Assert.Equal(before.ChangedAt, same.ChangedAt);
        Assert.True(same.ReadAt > before.ReadAt);

        await CommitAsync(project.Id, "alpha", "src/App/packages.lock.json", Lock.Replace("4.0.0", "4.1.0", StringComparison.Ordinal));
        _clock.Advance(TimeSpan.FromHours(1));
        await RefreshAsync(project.Id);
        var after = (await InventoriesAsync(project.Id)).Single(item => item.Origin == ProjectInventoryOrigin.Repository);
        Assert.True(after.ChangedAt > before.ChangedAt);
        Assert.Contains(after.Items, item => item is { PackageName: "serilog", Version: "4.1.0" });
        Assert.DoesNotContain(after.Items, item => item is { PackageName: "serilog", Version: "4.0.0" });
        Assert.Equal(pollyId, after.Items.Single(item => item.PackageName == "polly").Id);
        Assert.Equal(2, after.ItemCount);
    }

    /// <summary>The second trigger of the matching, from a real commit: the read is what starts it, and a read
    /// of the same content or a package moved past the fix leaves the first match as it was.</summary>
    [Fact]
    public async Task ARefresh_MatchesWhatTheCommitBrought_OnceOnly()
    {
        await ResetAndMigrateAsync();
        var (_, project) = await SeedProjectAsync("Matched");
        await SeedRepositoryAsync(project.Id, "matched");
        await CommitAsync(project.Id, "matched", "src/App/packages.lock.json", Lock);
        await using (var db = NewContext())
        {
            var advisory = new VulnerabilityAdvisory
            {
                Source = "GHSA",
                ExternalId = "GHSA-serilog",
                ModifiedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                AffectedPackages =
                [
                    new VulnerabilityAffectedPackage
                    {
                        Ecosystem = "NuGet",
                        PackageName = "serilog",
                        RangesJson = """[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"4.1.0"}]}]""",
                        FixedVersion = "4.1.0"
                    }
                ]
            };
            advisory.MatchedModifiedAt = advisory.ModifiedAt;
            db.VulnerabilityAdvisories.Add(advisory);
            await db.SaveChangesAsync(Ct);
        }

        await RefreshAsync(project.Id);
        await using (var db = NewContext())
        {
            var match = await db.VulnerabilityMatches.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((project.Id, project.OrganizationId, "serilog", "4.0.0"), (match.ProjectId, match.OrganizationId, match.PackageName, match.Version));
            Assert.Equal(VulnerabilityMatchStatus.Affected, match.Status);
        }

        await CommitAsync(project.Id, "matched", "README.md", "# Hello");
        _clock.Advance(TimeSpan.FromHours(1));
        await RefreshAsync(project.Id);
        await CommitAsync(project.Id, "matched", "src/App/packages.lock.json", Lock.Replace("4.0.0", "4.1.0", StringComparison.Ordinal));
        _clock.Advance(TimeSpan.FromHours(1));
        await RefreshAsync(project.Id);

        await using var check = NewContext();
        var kept = await check.VulnerabilityMatches.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("4.0.0", kept.Version);
        Assert.Null(kept.ProjectInventoryItemId);
    }

    /// <summary>A push and an SBOM reach the refresh through the queue, the daily pass directly: none of them
    /// may lose a stored read to the matching, and what it left is the feed cycle's sweep.</summary>
    [Fact]
    public async Task AMatchingFailure_FailsNoRefresh_AndStaysPendingForTheFeedCycle()
    {
        await ResetAndMigrateAsync();
        var (_, project) = await SeedProjectAsync("Failing");
        await SeedRepositoryAsync(project.Id, "failing");
        await CommitAsync(project.Id, "failing", "src/App/packages.lock.json", Lock);
        var broken = Substitute.For<IVulnerabilityMatchService>();
        broken.MatchChangedInventoriesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("matching down"));
        var logger = new ErrorLog();

        await using (var db = NewContext())
            await NewService(db, broken, logger).RefreshProjectAsync(project.Id, Ct);

        var stored = (await InventoriesAsync(project.Id)).Single(item => item.Origin == ProjectInventoryOrigin.Repository);
        Assert.Equal(2, stored.ItemCount);
        await broken.Received(1).MatchChangedInventoriesAsync(project.Id, Arg.Any<CancellationToken>());
        Assert.Equal("matching down", Assert.Single(logger.Errors).Message);
        Assert.Null(stored.MatchedChangedAt);

        await using var sweep = NewContext();
        await sweep.VulnerabilityAdvisories.AddAsync(new VulnerabilityAdvisory
        {
            Source = "GHSA",
            ExternalId = "GHSA-pending",
            ModifiedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            MatchedModifiedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            AffectedPackages = [new VulnerabilityAffectedPackage { Ecosystem = "NuGet", PackageName = "serilog", VersionsJson = """["4.0.0"]""" }]
        }, Ct);
        await sweep.SaveChangesAsync(Ct);
        Assert.Equal(1, await NewMatching(sweep).MatchChangedInventoriesAsync(null, Ct));
    }

    /// <summary>From the announced branch to the row: the handler, the trigger, the queue and its unit of
    /// work are the real ones. Only the announcement is written here, <c>MarkPushedAsync</c> raises it.</summary>
    [Fact]
    public async Task AnAnnouncedPush_ChangesTheStoredInventory_ThroughTheRealQueue()
    {
        await ResetAndMigrateAsync();
        var (_, project) = await SeedProjectAsync("Pushed");
        var repositoryId = await SeedRepositoryAsync(project.Id, "pushed");
        await CommitAsync(project.Id, "pushed", "packages.lock.json", Lock);
        await RefreshAsync(project.Id);

        var pushed = await CommitAsync(project.Id, "pushed", "packages.lock.json", Lock.Replace("8.0.0", "8.1.0", StringComparison.Ordinal));
        var queue = new BackgroundTaskQueue();
        var handler = new GitBranchInventoryHandler(new ProjectInventoryTrigger(queue, Options.Create(new VulnerabilityWatchOptions())));
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddScoped<IProjectInventoryService>(provider => NewService(provider.GetRequiredService<AppDbContext>()));
        await using var provider = services.BuildServiceProvider();

        await handler.HandleAsync(
            new GitPushProcessedEvent(project.Id, repositoryId, "alice", "pushed", "main", "pushed", [new GitRefUpdate("refs/heads/main", "old", pushed)]), Ct);

        Assert.Equal(1, queue.CurrentLength);
        var work = await queue.DequeueAsync(Ct);
        await using (var scope = provider.CreateAsyncScope()) await work(scope.ServiceProvider, Ct);
        var stored = (await InventoriesAsync(project.Id)).Single(item => item.Origin == ProjectInventoryOrigin.Repository);
        Assert.Equal(pushed, stored.CommitSha);
        Assert.Contains(stored.Items, item => item is { PackageName: "polly", Version: "8.1.0" });
    }

    [Fact]
    public async Task EveryProject_EndsWithACoverageAndAReason_EvenWithNothingToRead()
    {
        await ResetAndMigrateAsync();
        var (_, bare) = await SeedProjectAsync("Bare");
        var (_, addressOnly) = await SeedProjectAsync("Address");
        var (_, configuration) = await SeedProjectAsync("Configuration");
        var (_, broken) = await SeedProjectAsync("Broken");
        await using (var db = NewContext())
        {
            var stored = await db.Projects.SingleAsync(item => item.Id == addressOnly.Id, Ct);
            stored.RepositoryUrl = "https://example.test/acme/app.git";
            await db.SaveChangesAsync(Ct);
        }

        await SeedRepositoryAsync(configuration.Id, "system", isConfiguration: true);
        await SeedRepositoryAsync(broken.Id, "gone");
        Directory.Delete(DiskPath(broken.Id, "gone"), recursive: true);

        foreach (var id in new[] { bare.Id, addressOnly.Id, configuration.Id, broken.Id }) await RefreshAsync(id);

        Assert.Equal("no-repository=1", Repository(await InventoriesAsync(bare.Id)).Reason);
        Assert.Equal("repository-address-only=1", Repository(await InventoriesAsync(addressOnly.Id)).Reason);
        Assert.Equal("no-repository=1", Repository(await InventoriesAsync(configuration.Id)).Reason);
        Assert.Equal("repository-unreadable=1", Repository(await InventoriesAsync(broken.Id)).Reason);
        foreach (var id in new[] { bare.Id, addressOnly.Id, configuration.Id, broken.Id })
        {
            var rows = await InventoriesAsync(id);
            Assert.All(rows, row => Assert.Equal(ProjectInventoryCoverage.None, row.Coverage));
            Assert.Contains(rows, row => row is { Origin: ProjectInventoryOrigin.Sbom, Reason: "no-sbom=1" });
        }

        static ProjectInventory Repository(List<ProjectInventory> rows) => rows.Single(row => row.Origin == ProjectInventoryOrigin.Repository);
    }

    [Fact]
    public async Task TheLastRelease_IsReadApart_OnlyWhenTheBranchHasMovedOn_AndAnUnknownCommitSaysSo()
    {
        await ResetAndMigrateAsync();
        var (_, project) = await SeedProjectAsync("Released");
        await SeedRepositoryAsync(project.Id, "released");
        var released = await CommitAsync(project.Id, "released", "packages.lock.json", Lock);
        await using (var db = NewContext())
        {
            db.Releases.Add(new Release { ProjectId = project.Id, Version = "1.0", CommitHash = released, PublishedAt = _clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(Ct);
        }

        await RefreshAsync(project.Id);
        Assert.DoesNotContain(await InventoriesAsync(project.Id), item => item.Origin == ProjectInventoryOrigin.ReleaseCommit);

        await CommitAsync(project.Id, "released", "packages.lock.json", Lock.Replace("4.0.0", "5.0.0", StringComparison.Ordinal));
        await RefreshAsync(project.Id);
        var rows = await InventoriesAsync(project.Id);
        var release = rows.Single(item => item.Origin == ProjectInventoryOrigin.ReleaseCommit);
        Assert.Equal(released, release.CommitSha);
        Assert.Contains(release.Items, item => item is { PackageName: "serilog", Version: "4.0.0" });
        Assert.Contains(rows.Single(item => item.Origin == ProjectInventoryOrigin.Repository).Items, item => item is { PackageName: "serilog", Version: "5.0.0" });

        await using (var db = NewContext())
        {
            var stored = await db.Releases.SingleAsync(item => item.ProjectId == project.Id, Ct);
            stored.CommitHash = new string('a', 40);
            await db.SaveChangesAsync(Ct);
        }

        await RefreshAsync(project.Id);
        var unknown = (await InventoriesAsync(project.Id)).Single(item => item.Origin == ProjectInventoryOrigin.ReleaseCommit);
        Assert.Equal(ProjectInventoryCoverage.None, unknown.Coverage);
        Assert.Equal("release-commit-not-found=1", unknown.Reason);
    }

    [Fact]
    public async Task TheSbom_ComesFromTheLastRunOfThisProject_AndNeverFromAnotherOrganization()
    {
        await ResetAndMigrateAsync();
        var (organization, project) = await SeedProjectAsync("Mine");
        var (otherOrganization, other) = await SeedProjectAsync("Theirs");
        await SeedSbomAsync(organization.Id, project.Id, "commit-old", 1, "pkg:npm/old@1.0.0");
        await SeedSbomAsync(organization.Id, project.Id, "commit-new", 2, "pkg:nuget/Serilog@4.0.0", "pkg:deb/debian/libc6@2.36-9?upstream=glibc&distro=debian-12", "pkg:generic/openssl@3.0");
        await SeedSbomAsync(otherOrganization.Id, other.Id, "commit-other", 3, "pkg:npm/secret@1.0.0");

        await RefreshAsync(project.Id);
        await RefreshAsync(other.Id);

        var mine = (await InventoriesAsync(project.Id)).Single(item => item.Origin == ProjectInventoryOrigin.Sbom);
        Assert.Equal(ProjectInventoryCoverage.Partial, mine.Coverage);
        Assert.Equal("commit-new", mine.CommitSha);
        Assert.Equal("unmapped-component:generic=1", mine.Reason);
        Assert.Equal(["Debian:12/glibc", "NuGet/serilog"], mine.Items.Select(item => $"{item.Ecosystem}/{item.PackageName}").Order());
        Assert.All(mine.Items, item => Assert.Equal(organization.Id, item.OrganizationId));

        var theirs = (await InventoriesAsync(other.Id)).Single(item => item.Origin == ProjectInventoryOrigin.Sbom);
        Assert.Equal("secret", Assert.Single(theirs.Items).PackageName);
        Assert.All(theirs.Items, item => Assert.Equal(otherOrganization.Id, item.OrganizationId));

        await using var db = NewContext();
        var snapshot = await new ProjectInventoryRepository(db).GetLatestSbomAsync(project.Id, otherOrganization.Id, 100, Ct);
        Assert.Null(snapshot);
    }

    private async Task SeedSbomAsync(int organizationId, int projectId, string commit, int hour, params string[] packageUrls)
    {
        await using var db = NewContext();
        var at = new DateTime(2026, 10, 31, hour, 0, 0, DateTimeKind.Utc);
        var report = new AnalysisReport
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            ScannerKey = "syft",
            ScannerName = "Syft",
            ScannerVersion = "1",
            Category = AnalysisCategory.Sbom,
            CommitHash = commit,
            ContentHash = commit,
            CompletedAt = at,
            StartedAt = at,
            CreatedAt = at,
            Components = [.. packageUrls.Select(url => new AnalysisComponent
            {
                OrganizationId = organizationId, ProjectId = projectId, Name = url, Version = "1", PackageUrl = url,
                ComponentType = "library", CreatedAt = at
            })]
        };
        // What Syft really writes around the packages: every file of the image, and the system itself.
        report.Components.Add(new AnalysisComponent
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Name = "/usr/bin/env",
            Version = string.Empty,
            ComponentType = "file",
            CreatedAt = at
        });
        report.Components.Add(new AnalysisComponent
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Name = "ubuntu",
            Version = "24.04",
            ComponentType = "operating-system",
            CreatedAt = at
        });
        db.AnalysisReports.Add(report);
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheControlPass_ReadsWhatIsDue_AndAProjectThatFailsDoesNotStopTheOthers()
    {
        await ResetAndMigrateAsync();
        var (_, fresh) = await SeedProjectAsync("Fresh");
        await RefreshAsync(fresh.Id);
        var (_, pending) = await SeedProjectAsync("Pending");
        var (_, deleted) = await SeedProjectAsync("Deleted");
        await using (var db = NewContext())
        {
            (await db.Projects.SingleAsync(item => item.Id == deleted.Id, Ct)).DeletionRequestedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddScoped<IProjectInventoryRepository, ProjectInventoryRepository>();
        var failing = Substitute.For<IProjectInventoryService>();
        failing.RefreshProjectAsync(pending.Id, Arg.Any<CancellationToken>()).Returns(_ => throw new InvalidOperationException("broken"));
        services.AddScoped(provider => failing);
        await using var provider = services.BuildServiceProvider();
        var worker = new ProjectInventoryRefreshService(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new VulnerabilityWatchOptions()), _clock,
            NullLogger<ProjectInventoryRefreshService>.Instance,
            leaderLease: LeaderLeaseDouble.Leader());

        await worker.RunPassAsync(Ct);
        await failing.Received(1).RefreshProjectAsync(pending.Id, Arg.Any<CancellationToken>());
        await failing.DidNotReceive().RefreshProjectAsync(fresh.Id, Arg.Any<CancellationToken>());
        await failing.DidNotReceive().RefreshProjectAsync(deleted.Id, Arg.Any<CancellationToken>());

        // A day later the fresh project is due again.
        _clock.Advance(TimeSpan.FromHours(25));
        await worker.RunPassAsync(Ct);
        await failing.Received(1).RefreshProjectAsync(fresh.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheDatabase_RefusesASecondRowForTheSameSource_NullRepositoryIncluded()
    {
        await ResetAndMigrateAsync();
        var (organization, project) = await SeedProjectAsync("Unique");
        await using var db = NewContext();
        ProjectInventory Row() => new() { OrganizationId = organization.Id, ProjectId = project.Id, Origin = ProjectInventoryOrigin.Sbom };
        db.ProjectInventories.Add(Row());
        await db.SaveChangesAsync(Ct);

        db.ProjectInventories.Add(Row());

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task DeletingARepository_TakesItsInventoryAndItems()
    {
        await ResetAndMigrateAsync();
        var (_, project) = await SeedProjectAsync("Removed");
        var repositoryId = await SeedRepositoryAsync(project.Id, "removed");
        await CommitAsync(project.Id, "removed", "packages.lock.json", Lock);
        await RefreshAsync(project.Id);

        await using var db = NewContext();
        db.GitInternalRepos.Remove(await db.GitInternalRepos.SingleAsync(item => item.Id == repositoryId, Ct));
        await db.SaveChangesAsync(Ct);

        Assert.False(await db.ProjectInventories.AnyAsync(item => item.GitInternalRepoId == repositoryId, Ct));
        Assert.False(await db.ProjectInventoryItems.AnyAsync(item => item.ProjectId == project.Id, Ct));
    }
}
