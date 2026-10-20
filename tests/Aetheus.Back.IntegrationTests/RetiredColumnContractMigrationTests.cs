// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Exercises staged column retirement on PostgreSQL, including branch-advance drops and
/// the database default that permits writes to an unmapped encrypted-value column.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RetiredColumnContractMigrationTests(PostgresFixture fixture)
{
    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Migrate_DropsBranchAdvanceColumns_AndDefaultsTheUnmappedSecretHistoryValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AppDbContext(Options());
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);
        await db.Database.MigrateAsync(ct);

        var environmentColumns = await db.Database.SqlQueryRaw<string>(
            """
            SELECT column_name AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'Environments'
            """).ToListAsync(ct);
        Assert.NotEmpty(environmentColumns);
        Assert.DoesNotContain("AdvanceBranchOnDeploy", environmentColumns);
        Assert.DoesNotContain("AdvanceBranchName", environmentColumns);

        var encryptedValue = await db.Database.SqlQueryRaw<string>(
            """
            SELECT is_nullable || '|' || coalesce(column_default, '<none>') AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'VaultSecretVersions' AND column_name = 'EncryptedValue'
            """).SingleAsync(ct);
        Assert.Equal("NO|''::text", encryptedValue);
    }
}
