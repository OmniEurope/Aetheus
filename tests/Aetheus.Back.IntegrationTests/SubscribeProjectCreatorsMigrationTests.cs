// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aetheus.Back.IntegrationTests;

/// <summary> The <c>SubscribeProjectCreatorsToTheirProjects</c> backfill, run for real on
/// PostgreSQL. A project whose "Created" audit entry names an active user gets that user as a
/// follower; a project created by "system", by an inactive user or with no entry gets none; an
/// existing subscription is kept, and applying the migration again adds nothing. </summary>
[Collection(PostgresCollection.Name)]
public sealed class SubscribeProjectCreatorsMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "20261001101249_SubscribeProjectCreatorsToTheirProjects";

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Up_SubscribesEachIdentifiableCreator_Once_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var reset = new AppDbContext(Options()))
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);

        int alice, carol, shop, blog, docs, legacy, followed;
        await using (var db = new AppDbContext(Options()))
        {
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.IndexOf(Migration);
            Assert.True(index > 0, $"Could not locate {Migration} after a predecessor.");
            await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);

            var organizationId = await db.Organizations.OrderBy(o => o.Id).Select(o => o.Id).FirstAsync(ct);
            // The users go through the current model, which holds Users.LastActivityAt (20261028150000, after
            // this migration): the column is present while the rows are written, then dropped again so that
            // migration still runs for real afterwards (as ScopeRoleAssignmentsMigrationTests does).
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" ADD COLUMN \"LastActivityAt\" timestamp with time zone NULL", ct);
            var aliceUser = new User { Username = "alice-r2034", Email = "alice-r2034@example.test" };
            var carolUser = new User { Username = "carol-r2034", Email = "carol-r2034@example.test", IsActive = false };
            db.Users.AddRange(aliceUser, carolUser);
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" DROP COLUMN \"LastActivityAt\"", ct);
            var projects = new List<int>();
            foreach (var name in new[] { "Shop", "Blog", "Docs", "Legacy", "Followed" })
                projects.Add(await LegacyProjectRows.InsertAsync(db, name + "-r2034", organizationId, ct));
            (alice, carol) = (aliceUser.Id, carolUser.Id);
            (shop, blog, docs, legacy, followed) = (projects[0], projects[1], projects[2], projects[3], projects[4]);

            db.AuditLogs.AddRange(
                Created(shop, "alice-r2034"),
                // A later "Created" entry naming someone else does not change the creator.
                Created(shop, "carol-r2034"),
                Created(blog, "system"),
                Created(docs, "carol-r2034"),
                Created(followed, "alice-r2034"),
                new AuditLog { Username = "alice-r2034", Action = "Updated", EntityType = "Project", EntityId = legacy, Hash = Guid.NewGuid().ToString("N"), PreviousHash = Guid.NewGuid().ToString("N") });
            db.ProjectSubscriptions.Add(new ProjectSubscription { UserId = alice, ProjectId = followed });
            await db.SaveChangesAsync(ct);
        }

        await using (var db = new AppDbContext(Options()))
        {
            await db.Database.MigrateAsync(ct);
            // The deploy path re-runs migrations on every boot: applying it again must add nothing.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '" + Migration + "';", ct);
            await db.Database.MigrateAsync(ct);
        }

        await using (var db = new AppDbContext(Options()))
        {
            var rows = await db.ProjectSubscriptions.AsNoTracking()
                .Where(s => s.UserId == alice || s.UserId == carol)
                .OrderBy(s => s.ProjectId)
                .Select(s => new { s.UserId, s.ProjectId })
                .ToListAsync(ct);
            Assert.Equal([(alice, shop), (alice, followed)], rows.Select(row => (row.UserId, row.ProjectId)));
            Assert.DoesNotContain(rows, row => row.ProjectId == blog || row.ProjectId == docs || row.ProjectId == legacy);
        }
    }

    private static AuditLog Created(int projectId, string username) => new()
    {
        Username = username,
        Action = "Created",
        EntityType = "Project",
        EntityId = projectId,
        Details = "r2034",
        // The audit chain keeps every hash unique.
        Hash = Guid.NewGuid().ToString("N"),
        PreviousHash = Guid.NewGuid().ToString("N")
    };
}
