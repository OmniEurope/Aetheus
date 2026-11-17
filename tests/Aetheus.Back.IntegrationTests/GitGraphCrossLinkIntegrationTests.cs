// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Exercises the git-graph cross-linking schema against real PostgreSQL - the implicit M2M join
/// tables (<c>CommitReleases</c>, <c>BranchReleases</c>, <c>BranchCommits</c>) and the per-project
/// unique indexes on <c>GitCommit (ProjectId, Sha)</c> / <c>GitBranch (ProjectId, Name)</c> that the
/// <see cref="Aetheus.Back.Components.GitGraph.GitGraphRecorder"/> relies on for get-or-create.
/// The InMemory unit suite is blind to both: it neither materialises the join tables relationally
/// nor enforces the unique indexes.
/// </summary>
public sealed class GitGraphCrossLinkIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private static async Task<int> SeedProjectAsync(AppDbContext db, string slug)
    {
        var org = new Organization { Name = $"Org {slug}", Slug = slug, Description = "gitgraph test org" };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        var project = new Project { Name = $"Proj {slug}", Description = "d", OrganizationId = org.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    [Fact]
    public async Task CrossLinks_RoundTripThroughJoinTables()
    {
        await ResetAndMigrateAsync();
        int projectId;
        int releaseId;

        await using (var db = NewContext())
        {
            projectId = await SeedProjectAsync(db, "graph");

            var release = new Release { ProjectId = projectId, Version = "1.0.0", BranchName = "main", Status = ReleaseStatus.Published };
            var commit = new GitCommit { ProjectId = projectId, Sha = "deadbeef", Message = "init", CreatedAt = DateTime.UtcNow };
            var branch = new GitBranch { ProjectId = projectId, Name = "main", CreatedAt = DateTime.UtcNow };

            commit.Branches.Add(branch);
            release.Commits.Add(commit);
            release.Branches.Add(branch);

            db.Releases.Add(release);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            releaseId = release.Id;
        }

        await using (var verify = NewContext())
        {
            var release = await verify.Releases
                .Include(r => r.Commits)
                .Include(r => r.Branches)
                .AsSplitQuery()
                .FirstAsync(r => r.Id == releaseId, TestContext.Current.CancellationToken);

            Assert.Equal("deadbeef", Assert.Single(release.Commits).Sha);
            Assert.Equal("main", Assert.Single(release.Branches).Name);

            var commit = await verify.GitCommits.Include(c => c.Branches).FirstAsync(c => c.ProjectId == projectId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("main", Assert.Single(commit.Branches).Name);
        }
    }

    [Fact]
    public async Task GitCommit_DuplicateShaPerProject_ViolatesUniqueIndex()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var projectId = await SeedProjectAsync(db, "commitdup");

        db.GitCommits.Add(new GitCommit { ProjectId = projectId, Sha = "abc", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        db.GitCommits.Add(new GitCommit { ProjectId = projectId, Sha = "abc", CreatedAt = DateTime.UtcNow });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("23505", (ex.InnerException as PostgresException)?.SqlState);
    }

    [Fact]
    public async Task GitBranch_DuplicateNamePerProject_ViolatesUniqueIndex()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var projectId = await SeedProjectAsync(db, "branchdup");

        db.GitBranches.Add(new GitBranch { ProjectId = projectId, Name = "main", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        db.GitBranches.Add(new GitBranch { ProjectId = projectId, Name = "main", CreatedAt = DateTime.UtcNow });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("23505", (ex.InnerException as PostgresException)?.SqlState);
    }
}
