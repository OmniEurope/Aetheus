// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// ADR-059: the <c>ScopeRoleAssignmentsToOrganizations</c> data step, run for real on PostgreSQL over
/// rows of the old model (global UserRoles, membership levels). Each member gets the role of its level,
/// each former global role other than Admin is held for each of the user's organizations, the level is
/// raised to the highest role held (M3), and the legacy rows stay for a rollback.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopeRoleAssignmentsMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "20261028140000_ScopeRoleAssignmentsToOrganizations";

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Up_GivesEachMemberTheRolesOfTheOldModel_AndRaisesTheLevel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var reset = new AppDbContext(Options()))
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);

        int orgA, orgB, member, maintainer, owner, outsider, admin, contributorRole, customPermissionId;
        await using (var db = new AppDbContext(Options()))
        {
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.IndexOf(Migration);
            Assert.True(index > 0, $"Could not locate {Migration} after a predecessor.");
            await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);

            // The seed below goes through the current model, which holds columns added by migrations after this one
            // (Users.LastActivityAt, Roles.CreatedAt and UpdatedAt, 20261028150000; Roles.OrganizationId, 20261028200000; Organizations.AutoCreateDevelopBranch, 20261029072029); they are present while the rows are written and dropped again so
            // that migration still runs for real afterwards.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" ADD COLUMN \"LastActivityAt\" timestamp with time zone NULL", ct);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Roles\" ADD COLUMN \"CreatedAt\" timestamp with time zone NOT NULL DEFAULT now(), ADD COLUMN \"UpdatedAt\" timestamp with time zone NOT NULL DEFAULT now()", ct);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Roles\" ADD COLUMN \"OrganizationId\" integer NULL", ct);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Organizations\" ADD COLUMN \"AutoCreateDevelopBranch\" boolean NOT NULL DEFAULT false", ct);

            // The old model: global roles, an existing Contributor with one hand-edited permission.
            var adminRole = new Role { Name = "Admin", Description = "admin" };
            var contributor = new Role { Name = "Contributor", Description = "edited" };
            db.Roles.AddRange(adminRole, contributor);
            var a = new Organization { Name = "Org A", Slug = "org-a" };
            var b = new Organization { Name = "Org B", Slug = "org-b" };
            db.Organizations.AddRange(a, b);
            User NewUser(string name) => new() { Username = name, Email = $"{name}@example.test", PasswordHash = "x", IsActive = true };
            var users = new[] { NewUser("member"), NewUser("maintainer"), NewUser("owner"), NewUser("outsider"), NewUser("admin-member") };
            db.Users.AddRange(users);
            await db.SaveChangesAsync(ct);
            var custom = new ResourcePermission
            {
                RoleId = contributor.Id,
                ResourceType = ResourceType.Project,
                ResourceId = null,
                Permission = Permission.Read
            };
            db.ResourcePermissions.Add(custom);
            db.OrganizationMembers.AddRange(
                new OrganizationMember { UserId = users[0].Id, OrganizationId = a.Id, Role = OrganizationRole.Member },
                new OrganizationMember { UserId = users[0].Id, OrganizationId = b.Id, Role = OrganizationRole.Member },
                new OrganizationMember { UserId = users[1].Id, OrganizationId = a.Id, Role = OrganizationRole.Maintainer },
                new OrganizationMember { UserId = users[2].Id, OrganizationId = a.Id, Role = OrganizationRole.Owner },
                new OrganizationMember { UserId = users[4].Id, OrganizationId = a.Id, Role = OrganizationRole.Member });
            db.UserRoles.AddRange(
                new UserRole { UserId = users[0].Id, RoleId = contributor.Id },
                new UserRole { UserId = users[3].Id, RoleId = contributor.Id },
                new UserRole { UserId = users[4].Id, RoleId = adminRole.Id });
            await db.SaveChangesAsync(ct);
            (orgA, orgB, contributorRole, customPermissionId) = (a.Id, b.Id, contributor.Id, custom.Id);
            (member, maintainer, owner, outsider, admin) = (users[0].Id, users[1].Id, users[2].Id, users[3].Id, users[4].Id);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" DROP COLUMN \"LastActivityAt\"", ct);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Roles\" DROP COLUMN \"CreatedAt\", DROP COLUMN \"UpdatedAt\", DROP COLUMN \"OrganizationId\"", ct);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Organizations\" DROP COLUMN \"AutoCreateDevelopBranch\"", ct);
        }

        await using (var db = new AppDbContext(Options()))
            await db.Database.MigrateAsync(ct);

        await using (var db = new AppDbContext(Options()))
        {
            var held = await db.UserOrganizationRoles.AsNoTracking()
                .Select(item => new { item.UserId, item.OrganizationId, item.Role.Name })
                .ToListAsync(ct);
            HashSet<(int OrganizationId, string Role)> Of(int userId) =>
                held.Where(item => item.UserId == userId).Select(item => (item.OrganizationId, item.Name)).ToHashSet();

            // Member level = Reader, plus the former global Contributor in each of its organizations.
            Assert.Equal(new HashSet<(int, string)> { (orgA, "Reader"), (orgA, "Contributor"), (orgB, "Reader"), (orgB, "Contributor") }, Of(member));
            Assert.Equal(new HashSet<(int, string)> { (orgA, "Contributor") }, Of(maintainer));
            Assert.Equal(new HashSet<(int, string)> { (orgA, "Organization Admin") }, Of(owner));
            // No membership: the former global role reaches no organization.
            Assert.Empty(Of(outsider));
            // Admin stays site-wide only; its membership gives Reader.
            Assert.Equal(new HashSet<(int, string)> { (orgA, "Reader") }, Of(admin));

            // M3: the level follows the highest role now held.
            var levels = await db.OrganizationMembers.AsNoTracking()
                .ToDictionaryAsync(item => (item.UserId, item.OrganizationId), item => item.Role, ct);
            Assert.Equal(OrganizationRole.Maintainer, levels[(member, orgA)]);
            Assert.Equal(OrganizationRole.Maintainer, levels[(member, orgB)]);
            Assert.Equal(OrganizationRole.Maintainer, levels[(maintainer, orgA)]);
            Assert.Equal(OrganizationRole.Owner, levels[(owner, orgA)]);
            Assert.Equal(OrganizationRole.Member, levels[(admin, orgA)]);

            // The legacy rows stay for a rollback; the edited Contributor keeps its own permission only.
            Assert.Equal(3, await db.UserRoles.CountAsync(ct));
            Assert.Equal([customPermissionId], await db.ResourcePermissions
                .Where(item => item.RoleId == contributorRole).Select(item => item.Id).ToListAsync(ct));
            Assert.Equal(11, await db.ResourcePermissions.CountAsync(item => item.Role.Name == "Organization Admin", ct));
        }

        // The edited Contributor above must not leak into the collection's next host: start it clean.
        await using (var reset = new AppDbContext(Options()))
        {
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);
            await reset.Database.MigrateAsync(ct);
        }
    }
}
