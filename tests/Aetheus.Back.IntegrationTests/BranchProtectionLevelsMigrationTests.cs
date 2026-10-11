// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Decision D13 (BACK-013): the <c>BranchProtectionLevels</c> backfill, run for real on PostgreSQL. A
/// rule that required a pull request becomes ProtectedExceptPipelines (direct pushes still refused,
/// the advance-branch step of the release pipelines still allowed), every other rule NotProtected;
/// the RequirePullRequest column the previous colour reads is left as it was, and a level saved
/// through the entity afterwards keeps that column in step.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BranchProtectionLevelsMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "20261008102123_BranchProtectionLevels";

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Up_MapsRequirePullRequestToProtectedExceptPipelines_AndEveryOtherRuleToNotProtected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var reset = new AppDbContext(Options()))
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);

        int pullRequestRule, plainRule;
        await using (var db = new AppDbContext(Options()))
        {
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.IndexOf(Migration);
            Assert.True(index > 0, $"Could not locate {Migration} after a predecessor.");
            await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);

            var organizationId = await db.Organizations.OrderBy(o => o.Id).Select(o => o.Id).FirstAsync(ct);
            var projectId = await LegacyProjectRows.InsertAsync(db, "Protected-d13", organizationId, ct);
            // Raw rows: at this migration point the table has no ProtectionLevel column, which today's
            // entity would insert.
            var repoId = await InsertAsync(db,
                """
                INSERT INTO "GitInternalRepos" ("ProjectId", "Name", "Slug", "DefaultBranch", "IsEmpty", "CreatedAt", "UpdatedAt")
                VALUES (@project, 'Protected', 'protected-d13', 'main', false, now(), now())
                RETURNING "Id" AS "Value"
                """, ct, new NpgsqlParameter("project", projectId));
            pullRequestRule = await InsertRuleAsync(db, repoId, "main", requirePullRequest: true, ct);
            plainRule = await InsertRuleAsync(db, repoId, "release/*", requirePullRequest: false, ct);
        }

        await using (var db = new AppDbContext(Options()))
            await db.Database.MigrateAsync(ct);

        await using (var db = new AppDbContext(Options()))
        {
            var rules = await db.BranchProtectionRules.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
            Assert.Equal(BranchProtectionLevel.ProtectedExceptPipelines, rules[pullRequestRule].ProtectionLevel);
            Assert.True(rules[pullRequestRule].RequirePullRequest);
            Assert.Equal(BranchProtectionLevel.NotProtected, rules[plainRule].ProtectionLevel);
            Assert.False(rules[plainRule].RequirePullRequest);

            var tracked = await db.BranchProtectionRules.SingleAsync(r => r.Id == plainRule, ct);
            tracked.ProtectionLevel = BranchProtectionLevel.StrictlyProtected;
            await db.SaveChangesAsync(ct);
        }

        await using (var db = new AppDbContext(Options()))
        {
            var saved = await db.BranchProtectionRules.AsNoTracking().SingleAsync(r => r.Id == plainRule, ct);
            Assert.Equal(BranchProtectionLevel.StrictlyProtected, saved.ProtectionLevel);
            Assert.True(saved.RequirePullRequest);
        }
    }

    private static Task<int> InsertRuleAsync(
        AppDbContext db, int repoId, string pattern, bool requirePullRequest, CancellationToken ct) =>
        InsertAsync(db,
            """
            INSERT INTO "BranchProtectionRules" ("GitInternalRepoId", "Pattern", "PreventDeletion", "PreventForcePush", "RequirePullRequest", "CreatedAt")
            VALUES (@repo, @pattern, true, true, @requirePullRequest, now())
            RETURNING "Id" AS "Value"
            """, ct,
            new NpgsqlParameter("repo", repoId),
            new NpgsqlParameter("pattern", pattern),
            new NpgsqlParameter("requirePullRequest", requirePullRequest));

    private static async Task<int> InsertAsync(
        AppDbContext db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters) =>
        (await db.Database.SqlQueryRaw<int>(sql, parameters).ToListAsync(ct)).Single();
}
