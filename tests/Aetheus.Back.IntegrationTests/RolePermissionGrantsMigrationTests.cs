// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-11: the <c>AddRolePermissionGrants</c> backfill, run for real on PostgreSQL over the roles the
/// previous migrations leave (built-in Reader, Contributor and Organization Admin) plus a custom role
/// scoped to one server. Each role receives the named permissions its levels allowed before, on the same
/// scope, except the container environment the owner moved to Organization Admin; the rollback drops
/// the new table and leaves the levels as they were.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RolePermissionGrantsMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "20261028180000_AddRolePermissionGrants";
    private const int ScopedServer = 5;

    private static readonly string[] WriteKeys =
    [
        "pipeline.run", "pipeline.approval.decide", "release.rollback", "release.status.restore",
        "vault.secret.reveal", "server.services.control", "server.firewall.manage", "server.system.upgrade",
        "server.ports.reserve", "server.mailbox.manage"
    ];

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Up_GivesEachRoleTheActionsItsLevelsAllowed_ExceptTheContainerEnvironmentForReaderAndContributor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var reset = new AppDbContext(Options()))
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);

        string predecessor;
        int levelRowsBefore;
        await using (var db = new AppDbContext(Options()))
        {
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.IndexOf(Migration);
            Assert.True(index > 0, $"Could not locate {Migration} after a predecessor.");
            predecessor = migrations[index - 1];
            await db.GetService<IMigrator>().MigrateAsync(predecessor, ct);

            // The seed below goes through the current model, which holds Roles.OrganizationId (20261028200000, after this
            // migration): the column is present while the rows are written and dropped again so that migration still runs.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Roles\" ADD COLUMN \"OrganizationId\" integer NULL", ct);

            // Built-in Reader, Contributor and Organization Admin come from the earlier migrations (every
            // resource type at Read, Write, Admin). Add the site Admin without levels, and a custom role
            // writing one server only and reading pipelines.
            var serverContributor = new Role { Name = "Server contributor", Description = "custom" };
            db.Roles.AddRange(new Role { Name = "Admin", Description = "admin" }, serverContributor);
            await db.SaveChangesAsync(ct);
            db.ResourcePermissions.AddRange(
                new ResourcePermission { RoleId = serverContributor.Id, ResourceType = ResourceType.Server, ResourceId = ScopedServer, Permission = Permission.Write },
                new ResourcePermission { RoleId = serverContributor.Id, ResourceType = ResourceType.Pipeline, ResourceId = null, Permission = Permission.Read });
            await db.SaveChangesAsync(ct);
            levelRowsBefore = await db.ResourcePermissions.CountAsync(ct);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Roles\" DROP COLUMN \"OrganizationId\"", ct);
        }

        // Up to this migration only: a later one adds its own grants (org.roles.manage and org.mailtemplates.manage
        // for the Organization Admin and Admin roles, 20261028200000), which are not this migration's data step.
        await using (var db = new AppDbContext(Options()))
            await db.GetService<IMigrator>().MigrateAsync(Migration, ct);

        await using (var db = new AppDbContext(Options()))
        {
            var grants = await db.RolePermissionGrants.AsNoTracking()
                .Select(grant => new { grant.Role.Name, grant.PermissionKey, grant.ResourceId })
                .ToListAsync(ct);
            HashSet<(string Key, int? ResourceId)> Of(string role) =>
                grants.Where(grant => grant.Name == role).Select(grant => (grant.PermissionKey, grant.ResourceId)).ToHashSet();
            static HashSet<(string, int?)> RoleWide(params string[] keys) => keys.Select(key => (key, (int?)null)).ToHashSet();

            // Reader: only what Read allowed, and no longer the container environment.
            Assert.Equal(RoleWide("server.mailbox.read"), Of("Reader"));
            // Contributor: every Write action plus mailbox reading, except what the owner moved to Organization
            // Admin: the container environment, the firewall and the system upgrade.
            Assert.Equal(RoleWide([.. WriteKeys.Except(["server.firewall.manage", "server.system.upgrade"]), "server.mailbox.read"]),
                Of("Contributor"));
            // Organization Admin: everything, the container environment and member management included.
            Assert.Equal(RoleWide([.. WriteKeys, "server.mailbox.read", "server.docker.container.env.view", "org.members.manage"]),
                Of("Organization Admin"));
            // The site Admin bypasses every check; it holds member management like the role it replaces.
            Assert.Equal(RoleWide("org.members.manage"), Of("Admin"));
            // The custom role keeps exactly its rights, on the one server it held them for; reading
            // pipelines never allowed running them.
            Assert.Equal(new HashSet<(string, int?)>
            {
                ("server.services.control", ScopedServer), ("server.firewall.manage", ScopedServer),
                ("server.system.upgrade", ScopedServer), ("server.ports.reserve", ScopedServer),
                ("server.docker.container.env.view", ScopedServer), ("server.mailbox.read", ScopedServer),
                ("server.mailbox.manage", ScopedServer)
            }, Of("Server contributor"));

            // Expand only: the levels are untouched.
            Assert.Equal(levelRowsBefore, await db.ResourcePermissions.CountAsync(ct));
        }

        // Rollback: Down drops the new table only, the levels stay.
        await using (var db = new AppDbContext(Options()))
        {
            await db.GetService<IMigrator>().MigrateAsync(predecessor, ct);
            Assert.Equal(levelRowsBefore, await db.ResourcePermissions.CountAsync(ct));
            var table = await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'RolePermissionGrants'").SingleAsync(ct);
            Assert.Equal(0, table);
        }

        // The roles above must not leak into the collection's next host: start it clean.
        await using (var reset = new AppDbContext(Options()))
        {
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);
            await reset.Database.MigrateAsync(ct);
        }
    }
}
