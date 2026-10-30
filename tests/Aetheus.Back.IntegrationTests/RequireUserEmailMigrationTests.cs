// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-003 P3-4: <c>RequireUserEmail</c> run for real on PostgreSQL from the migration before it, over rows
/// written in SQL with the schema of that point. Clean accounts migrate and the column becomes NOT NULL; an
/// account without an address, or two sharing one in any case, stop the migration with their ids named, and
/// the rows and the column stay as they were. No address is invented and no account is removed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RequireUserEmailMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "20261028210000_RequireUserEmail";

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task CleanAccounts_Migrate_AndTheColumnBecomesNotNull_WithTheNotBlankCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        await MigrateToPredecessorAsync(ct);
        await using (var db = new AppDbContext(Options()))
        {
            Assert.Equal("YES", await EmailNullableAsync(db, ct));
            await InsertUserAsync(db, "alice", "alice@example.com", ct);
            await InsertUserAsync(db, "bob", "bob@example.com", ct);
        }

        await using (var db = new AppDbContext(Options()))
            await db.GetService<IMigrator>().MigrateAsync(Migration, ct);

        await using (var db = new AppDbContext(Options()))
        {
            Assert.Contains(Migration, await db.Database.GetAppliedMigrationsAsync(ct));
            Assert.Equal("NO", await EmailNullableAsync(db, ct));
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM pg_constraint WHERE conname = 'CK_Users_Email_NotBlank'").SingleAsync(ct));
            // The accounts are untouched, read through the model whose e-mail is now required.
            var emails = await db.Users.AsNoTracking().OrderBy(u => u.Username).Select(u => u.Email).ToListAsync(ct);
            Assert.Equal(["alice@example.com", "bob@example.com"], emails);
        }

        await ResetAsync(ct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task AnAccountWithoutAnEmail_StopsTheMigration_NamingItsId_AndNothingChanges(string? missing)
    {
        var ct = TestContext.Current.CancellationToken;
        await MigrateToPredecessorAsync(ct);
        int withoutId;
        await using (var db = new AppDbContext(Options()))
        {
            await InsertUserAsync(db, "alice", "alice@example.com", ct);
            await InsertUserAsync(db, "legacy", missing, ct);
            withoutId = await IdOfAsync(db, "legacy", ct);
        }

        await using (var db = new AppDbContext(Options()))
        {
            var stopped = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Migration, ct));
            Assert.Contains("Required sign-in e-mail migration blocked, nothing was changed.", stopped.MessageText, StringComparison.Ordinal);
            Assert.Contains($"Accounts without an e-mail address (user ids): {withoutId}.", stopped.MessageText, StringComparison.Ordinal);
            Assert.Contains("(user ids per address): none.", stopped.MessageText, StringComparison.Ordinal);
        }

        await using (var db = new AppDbContext(Options()))
        {
            Assert.DoesNotContain(Migration, await db.Database.GetAppliedMigrationsAsync(ct));
            Assert.Equal("YES", await EmailNullableAsync(db, ct));
            Assert.Equal(missing ?? NoEmail, await EmailOfAsync(db, "legacy", ct));
            Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"Users\"").SingleAsync(ct));
        }

        await ResetAsync(ct);
    }

    [Fact]
    public async Task TwoAccountsSharingAnEmailInAnyCase_StopTheMigration_NamingBothIds_AndNothingChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await MigrateToPredecessorAsync(ct);
        int firstId, secondId;
        await using (var db = new AppDbContext(Options()))
        {
            await InsertUserAsync(db, "first", "shared@example.com", ct);
            await InsertUserAsync(db, "second", " SHARED@Example.com", ct);
            await InsertUserAsync(db, "other", "other@example.com", ct);
            firstId = await IdOfAsync(db, "first", ct);
            secondId = await IdOfAsync(db, "second", ct);
        }

        await using (var db = new AppDbContext(Options()))
        {
            var stopped = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Migration, ct));
            Assert.Contains("Accounts without an e-mail address (user ids): none.", stopped.MessageText, StringComparison.Ordinal);
            Assert.Contains($"(user ids per address): {firstId}, {secondId}.", stopped.MessageText, StringComparison.Ordinal);
            Assert.DoesNotContain("shared@example.com", stopped.MessageText, StringComparison.OrdinalIgnoreCase);
        }

        await using (var db = new AppDbContext(Options()))
        {
            Assert.DoesNotContain(Migration, await db.Database.GetAppliedMigrationsAsync(ct));
            Assert.Equal("YES", await EmailNullableAsync(db, ct));
            Assert.Equal("shared@example.com", await EmailOfAsync(db, "first", ct));
            Assert.Equal(" SHARED@Example.com", await EmailOfAsync(db, "second", ct));
        }

        await ResetAsync(ct);
    }

    private const string NoEmail = "<null>";

    private async Task MigrateToPredecessorAsync(CancellationToken ct)
    {
        await using var db = new AppDbContext(Options());
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);
        var migrations = db.Database.GetMigrations().ToList();
        var index = migrations.IndexOf(Migration);
        Assert.True(index > 0, $"Could not locate {Migration} after a predecessor.");
        await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);
    }

    /// <summary>The rows above must not leak into the collection's next host: start it clean.</summary>
    private async Task ResetAsync(CancellationToken ct)
    {
        await using var reset = new AppDbContext(Options());
        await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);
        await reset.Database.MigrateAsync(ct);
    }

    private static Task<string> EmailNullableAsync(AppDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<string>(
            "SELECT is_nullable::text AS \"Value\" FROM information_schema.columns WHERE table_name = 'Users' AND column_name = 'Email'")
            .SingleAsync(ct);

    private static Task<int> IdOfAsync(AppDbContext db, string username, CancellationToken ct) =>
        db.Database.SqlQueryRaw<int>(
            "SELECT \"Id\" AS \"Value\" FROM \"Users\" WHERE \"Username\" = @username",
            new NpgsqlParameter("username", username)).SingleAsync(ct);

    private static Task<string> EmailOfAsync(AppDbContext db, string username, CancellationToken ct) =>
        db.Database.SqlQueryRaw<string>(
            "SELECT coalesce(\"Email\", '<null>') AS \"Value\" FROM \"Users\" WHERE \"Username\" = @username",
            new NpgsqlParameter("username", username)).SingleAsync(ct);

    /// <summary>The columns of "Users" at the predecessor migration, written in SQL so the seed matches that schema.</summary>
    private static Task<int> InsertUserAsync(AppDbContext db, string username, string? email, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Users" ("Username", "PasswordHash", "Email", "IsActive", "CreatedAt", "UpdatedAt",
                "MustChangePassword", "SecurityStamp", "FailedLoginCount", "TotpEnabled")
            VALUES (@username, 'x', @email, true, now(), now(), false, 'stamp', 0, false)
            """,
            [new NpgsqlParameter("username", username), new NpgsqlParameter("email", (object?)email ?? DBNull.Value)],
            ct);
}
