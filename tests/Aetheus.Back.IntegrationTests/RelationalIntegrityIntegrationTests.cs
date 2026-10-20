// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Exercises PostgreSQL uniqueness, RESTRICT and CASCADE constraints, including the
/// partial ProjectServer index. EF InMemory cannot prove these relational guarantees.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RelationalIntegrityIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private static Organization NewOrg(string slug) => new()
    {
        Name = $"Org {slug}",
        Slug = slug,
        Description = "rel-integrity test org",
    };

    /// <summary>
    /// Asserts the action throws a <see cref="DbUpdateException"/> whose root cause is a
    /// PostgreSQL unique-violation (SQLSTATE 23505). InMemory would let the duplicate through.
    /// </summary>
    private static async Task AssertUniqueViolationAsync(Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(act);
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
    }

    /// <summary>
    /// Asserts the action throws a <see cref="DbUpdateException"/> whose root cause is a
    /// PostgreSQL foreign-key integrity violation. PostgreSQL distinguishes two SQLSTATEs:
    /// a generic FK violation (23503 - e.g. inserting a child with no parent, the production
    /// incident signature) and a RESTRICT violation (23001 - a <c>ON DELETE RESTRICT</c> FK
    /// blocking a parent delete, which is how the org-scoped owner FKs behave). Both prove
    /// the relational constraint is enforced; InMemory enforces neither.
    /// </summary>
    private static async Task AssertForeignKeyViolationAsync(Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(act);
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Contains(
            pg.SqlState,
            new[] { PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.RestrictViolation });
    }

    [Fact]
    public async Task Organization_DuplicateSlug_ViolatesUniqueIndex()
    {
        // Arrange
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        db.Organizations.Add(NewOrg("acme"));
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Act + Assert - same slug, different name: only the Slug unique index can catch this,
        // and only on a real relational provider.
        db.Organizations.Add(new Organization { Name = "Totally Different", Slug = "acme" });
        await AssertUniqueViolationAsync(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task User_DuplicateUsername_ViolatesUniqueIndex()
    {
        // Arrange
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        db.Users.Add(new User { Username = "alice", PasswordHash = "h1" });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Act + Assert
        db.Users.Add(new User { Username = "alice", PasswordHash = "h2" });
        await AssertUniqueViolationAsync(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Role_DuplicateName_ViolatesUniqueIndex()
    {
        // Arrange
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        db.Roles.Add(new Role { Name = "Admin", Description = "first" });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Act + Assert
        db.Roles.Add(new Role { Name = "Admin", Description = "dup" });
        await AssertUniqueViolationAsync(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Project_DuplicateName_ViolatesUniqueIndex()
    {
        // Arrange
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var org = NewOrg("proj-dup");
        db.Organizations.Add(org);
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        db.Projects.Add(new Project { Name = "Phoenix", Description = "d", OrganizationId = org.Id });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Act + Assert
        db.Projects.Add(new Project { Name = "Phoenix", Description = "other", OrganizationId = org.Id });
        await AssertUniqueViolationAsync(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task OrganizationMember_DuplicateOrgUserPair_ViolatesCompositeUniqueIndex()
    {
        // Arrange
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var org = NewOrg("members");
        db.Organizations.Add(org);
        var user = new User { Username = "bob", PasswordHash = "h" };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Role = OrganizationRole.Owner,
        });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Act + Assert - second membership for the same (org, user) pair must be rejected by
        // the composite unique index, even with a different role.
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Role = OrganizationRole.Member,
        });
        await AssertUniqueViolationAsync(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Project_InsertWithDanglingOrganizationId_ViolatesForeignKey()
    {
        // Arrange - empty Organizations table.
        await ResetAndMigrateAsync();
        await using var db = NewContext();

        // Act + Assert - this is the precise production failure (23503): a Project row pointing
        // at an OrganizationId that does not exist. InMemory accepts it silently.
        db.Projects.Add(new Project { Name = "Orphan", Description = "d", OrganizationId = 999_999 });
        await AssertForeignKeyViolationAsync(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DeleteOrganization_WithOwnedProject_IsBlockedByRestrictForeignKey()
    {
        // Arrange
        await ResetAndMigrateAsync();
        int orgId;
        await using (var db = NewContext())
        {
            var org = NewOrg("restrict-proj");
            db.Organizations.Add(org);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            db.Projects.Add(new Project { Name = "Owned", Description = "d", OrganizationId = org.Id });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            orgId = org.Id;
        }

        // Act + Assert - Project.Organization is OnDelete(Restrict). The delete is issued from
        // a FRESH context that has NOT loaded the child Project, so EF emits a bare
        // `DELETE FROM "Organizations"` and the database's RESTRICT FK rejects it with 23503 -
        // the exact production failure class. (If the child were tracked, EF would sever the
        // required relationship in-memory and never let the DELETE reach PostgreSQL; InMemory
        // has no RESTRICT concept at all and would simply orphan the project.)
        await using var del = NewContext();
        var orgToDelete = await del.Organizations.SingleAsync(o => o.Id == orgId, cancellationToken: TestContext.Current.CancellationToken);
        del.Organizations.Remove(orgToDelete);
        await AssertForeignKeyViolationAsync(() => del.SaveChangesAsync());
    }

    [Fact]
    public async Task DeleteOrganization_WithOwnedServer_IsBlockedByRestrictForeignKey()
    {
        // Arrange
        await ResetAndMigrateAsync();
        int orgId;
        await using (var db = NewContext())
        {
            var org = NewOrg("restrict-srv");
            db.Organizations.Add(org);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            db.Servers.Add(new Server { Name = "node-1", Hostname = "h", OrganizationId = org.Id });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            orgId = org.Id;
        }

        // Act + Assert - Server.Organization is also OnDelete(Restrict); same fresh-context
        // rationale as above so the real PostgreSQL constraint fires.
        await using var del = NewContext();
        var orgToDelete = await del.Organizations.SingleAsync(o => o.Id == orgId, cancellationToken: TestContext.Current.CancellationToken);
        del.Organizations.Remove(orgToDelete);
        await AssertForeignKeyViolationAsync(() => del.SaveChangesAsync());
    }

    [Fact]
    public async Task DeleteOrganization_CascadesMembershipRows()
    {
        // Arrange
        await ResetAndMigrateAsync();
        int orgId;
        int userId;
        await using (var db = NewContext())
        {
            var org = NewOrg("cascade-members");
            db.Organizations.Add(org);
            var user = new User { Username = "carol", PasswordHash = "h" };
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = org.Id,
                UserId = user.Id,
                Role = OrganizationRole.Owner,
            });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            orgId = org.Id;
            userId = user.Id;
        }

        // Act - delete the org from a FRESH context that has NOT loaded the membership row, so
        // the row removal is performed by PostgreSQL's ON DELETE CASCADE itself (not EF's
        // in-memory cascade simulation). The org has no owned Project/Server so the delete is
        // allowed; its membership rows must cascade away while the User row survives (the
        // cascade is org -> member, not org -> user). InMemory cannot model DB-side cascades.
        await using (var del = NewContext())
        {
            var orgToDelete = await del.Organizations.SingleAsync(o => o.Id == orgId, cancellationToken: TestContext.Current.CancellationToken);
            del.Organizations.Remove(orgToDelete);
            await del.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        // Assert
        await using var verify = NewContext();
        Assert.False(await verify.Organizations.AnyAsync(o => o.Id == orgId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await verify.OrganizationMembers.AnyAsync(m => m.OrganizationId == orgId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await verify.Users.AnyAsync(u => u.Id == userId, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProjectServer_FilteredUniqueIndex_BlocksDuplicateRealServerLink_ButAllowsMultipleNullServerRows()
    {
        // Arrange
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var org = NewOrg("proj-server");
        db.Organizations.Add(org);
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var project = new Project { Name = "Linked", Description = "d", OrganizationId = org.Id };
        db.Projects.Add(project);
        var server = new Server { Name = "link-node", Hostname = "h", OrganizationId = org.Id };
        db.Servers.Add(server);
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        db.ProjectServers.Add(new ProjectServer
        {
            ProjectId = project.Id,
            ServerId = server.Id,
            Type = ProjectServerType.AgentServer,
            DisplayName = "first link",
        });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Act + Assert (1) - same (ProjectId, ServerId) again: the partial unique index
        // (WHERE "ServerId" IS NOT NULL) must reject it. This filtered index is a
        // PostgreSQL-specific construct that InMemory cannot model at all.
        db.ProjectServers.Add(new ProjectServer
        {
            ProjectId = project.Id,
            ServerId = server.Id,
            Type = ProjectServerType.AgentServer,
            DisplayName = "dup link",
        });
        await AssertUniqueViolationAsync(() => db.SaveChangesAsync());

        // Reset the change tracker (the failed entry is still tracked).
        db.ChangeTracker.Clear();

        // Act + Assert (2) - multiple rows with ServerId NULL for the same project are allowed
        // BECAUSE the unique index is filtered on "ServerId IS NOT NULL".
        db.ProjectServers.AddRange(
            new ProjectServer { ProjectId = project.Id, ServerId = null, Type = ProjectServerType.ExternalHost, DisplayName = "ext-1", Host = "a.example.com" },
            new ProjectServer { ProjectId = project.Id, ServerId = null, Type = ProjectServerType.ExternalHost, DisplayName = "ext-2", Host = "b.example.com" });
        var ex = await Record.ExceptionAsync(() => db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(ex);
    }
}
