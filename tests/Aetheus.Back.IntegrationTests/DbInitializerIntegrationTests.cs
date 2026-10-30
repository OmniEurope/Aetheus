// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// End-to-end exercise of the REAL <see cref="DbInitializer.SeedAsync"/> on real PostgreSQL.
/// <para>
/// This is a documented InMemory blind spot. The InMemory unit suite never calls
/// <c>DbInitializer.SeedAsync</c> at all - it mocks <c>IOrganizationRepository</c> instead -
/// so the actual seed path (its relational transaction, the <c>IsRelational()</c> weak-password
/// guard, idempotency via <c>AppSettings.AnyAsync()</c>, and the default <c>Aetheus</c>
/// organization + owner membership) has had zero coverage. Worse, <c>SeedAsync</c> wraps its
/// writes in <c>BeginTransactionAsync()</c> only when <c>Database.IsRelational()</c> - a branch
/// that, by construction, InMemory can never take. These tests run it for real.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbInitializerIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private const string StrongAdminPassword = "Integr@tion-Seed-Admin-Pwd-2026";

    private static IConfiguration ConfigWith(string? adminPassword)
    {
        var values = new Dictionary<string, string?>();
        if (adminPassword is not null)
            values["Auth:AdminPassword"] = adminPassword;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public async Task SeedAsync_OnFreshDatabase_CreatesBootstrapAdminUserRolesAndDefaultOrg()
    {
        // Arrange - pristine, fully-migrated schema; no seed data yet.
        await ResetAndMigrateAsync();
        await using var db = NewContext();

        // Act - run the production seed path against real PostgreSQL.
        await DbInitializer.SeedAsync(db, ConfigWith(StrongAdminPassword));

        // Assert - bootstrap admin user exists and is active.
        await using var verify = NewContext();
        var admin = await verify.Users.SingleAsync(u => u.Username == "admin", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(admin.IsActive);
        Assert.False(string.IsNullOrWhiteSpace(admin.PasswordHash));
        Assert.NotEqual(StrongAdminPassword, admin.PasswordHash); // BCrypt-hashed, never plaintext.
        // PLAN-003 P3-2: it signs in by e-mail; without Auth:AdminEmail the seed's default address.
        Assert.Equal("admin@aetheus.local", admin.Email);

        // The three default roles are created.
        var roleNames = await verify.Roles.Select(r => r.Name).OrderBy(n => n).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        // R-10 (ADR-059): Organization Admin is the fourth built-in role, managing one organization.
        Assert.Equal(["Admin", "Contributor", "Organization Admin", "Reader"], roleNames);

        // Admin user is bound to the Admin role.
        var adminRole = await verify.Roles.SingleAsync(r => r.Name == "Admin", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await verify.UserRoles.AnyAsync(ur => ur.UserId == admin.Id && ur.RoleId == adminRole.Id, cancellationToken: TestContext.Current.CancellationToken));

        // The default "Aetheus" organization is seeded.
        var org = await verify.Organizations.SingleAsync(o => o.Slug == "aetheus", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Aetheus", org.Name);

        // Admin owns the default organization.
        var membership = await verify.OrganizationMembers
            .SingleAsync(m => m.OrganizationId == org.Id && m.UserId == admin.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(OrganizationRole.Owner, membership.Role);

        // F-23: the Admin role has an Admin-level ResourcePermission for every ResourceType.
        var expectedResourceTypes = Enum.GetValues<ResourceType>().Length;
        var adminPermissionCount = await verify.ResourcePermissions
            .CountAsync(p => p.RoleId == adminRole.Id && p.Permission == Permission.Admin && p.ResourceId == null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expectedResourceTypes, adminPermissionCount);

        // App settings + pipeline templates were seeded too.
        Assert.True(await verify.AppSettings.AnyAsync(s => s.Key == "SiteName", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await verify.PipelineTemplates.AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedAsync_RunTwice_IsIdempotent_NoDuplicateBootstrapRows()
    {
        // Arrange
        await ResetAndMigrateAsync();

        // Act - first seed, then a SECOND seed on a fresh context (the deploy entrypoint
        // re-runs seeding on every boot; the AppSettings.AnyAsync() guard must short-circuit).
        await using (var first = NewContext())
            await DbInitializer.SeedAsync(first, ConfigWith(StrongAdminPassword));

        await using (var second = NewContext())
            await DbInitializer.SeedAsync(second, ConfigWith(StrongAdminPassword));

        // Assert - exactly one of every bootstrap row, no duplicates from the second run.
        await using var verify = NewContext();
        Assert.Equal(1, await verify.Users.CountAsync(u => u.Username == "admin", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.Organizations.CountAsync(o => o.Slug == "aetheus", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.Roles.CountAsync(r => r.Name == "Admin", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.Roles.CountAsync(r => r.Name == "Reader", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.Roles.CountAsync(r => r.Name == "Contributor", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.Roles.CountAsync(r => r.Name == "Organization Admin", cancellationToken: TestContext.Current.CancellationToken));

        var admin = await verify.Users.SingleAsync(u => u.Username == "admin", cancellationToken: TestContext.Current.CancellationToken);
        var org = await verify.Organizations.SingleAsync(o => o.Slug == "aetheus", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, await verify.OrganizationMembers
            .CountAsync(m => m.OrganizationId == org.Id && m.UserId == admin.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedAsync_OnRelationalDb_WithWeakAdminPassword_Throws_AndSeedsNothing()
    {
        // Arrange
        await ResetAndMigrateAsync();
        await using var db = NewContext();

        // Act + Assert - F-06: on a relational DB a missing/weak admin password must throw.
        // (InMemory is explicitly exempted by the IsRelational() check, so this guard is
        // unobservable in the unit suite.)
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DbInitializer.SeedAsync(db, ConfigWith("admin")));

        // The seed writes AppSettings (and SaveChanges) inside its relational transaction
        // BEFORE reaching the weak-password guard. Because the guard throws, the surrounding
        // `await using` transaction is disposed without a commit, so PostgreSQL must roll the
        // AppSettings rows back, and no admin user is created. (InMemory could never exercise
        // this: it does not open the transaction at all.)
        await using var verify = NewContext();
        Assert.False(await verify.Users.AnyAsync(u => u.Username == "admin", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await verify.AppSettings.AnyAsync(cancellationToken: TestContext.Current.CancellationToken));

        // NOTE: the "aetheus" Organization legitimately EXISTS at this point - it is seeded
        // by the AddOrganizationOwnership migration's own SQL (idempotent backfill), entirely
        // independent of DbInitializer. So it is intentionally NOT asserted absent here; this
        // very fact (migration seeds the org, initializer reuses it) is what
        // SeedAsync_OnFreshDatabase / RunTwice cover positively.
    }

    [Fact]
    public async Task SeedAsync_BuildTemplatesRunTheTestsUnderMicrosoftTestingPlatform()
    {
        // The repository's global.json runs dotnet test on Microsoft.Testing.Platform, which refuses the
        // VSTest switches (--logger, --collect) the seeded templates still carried.
        await ResetAndMigrateAsync();
        await using (var db = NewContext())
            await DbInitializer.SeedAsync(db, ConfigWith(StrongAdminPassword));

        await using var verify = NewContext();
        var yamls = await verify.PipelineTemplateVersions
            .Where(version => version.YamlContent.Contains("dotnet test"))
            .Select(version => version.YamlContent)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, yamls.Count);
        foreach (var yaml in yamls)
        {
            var definition = Aetheus.Back.Services.YamlParsingHelper.Deserializer
                .Deserialize<Aetheus.Shared.Components.Pipelines.PipelineYamlDefinition>(yaml);
            var commands = definition.Stages.SelectMany(stage => stage.Steps)
                .Select(step => step.Shell ?? string.Empty)
                .Where(shell => shell.Contains("dotnet test", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(4, commands.Count);
            Assert.All(commands, shell =>
            {
                Assert.Contains("dotnet test --project ", shell, StringComparison.Ordinal);
                Assert.DoesNotContain("--logger", shell, StringComparison.Ordinal);
                Assert.DoesNotContain("--collect", shell, StringComparison.Ordinal);
            });
        }
    }
}
