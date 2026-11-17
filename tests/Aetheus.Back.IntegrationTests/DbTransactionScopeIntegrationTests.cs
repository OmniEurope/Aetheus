// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Real relational-transaction semantics for <see cref="DbTransactionScope"/>.
/// <para>
/// The InMemory unit test (<c>Aetheus.Back.Tests/Shared/DbTransactionScopeTests.cs</c>)
/// can only assert the degenerate "no transaction was started" path: InMemory does not
/// support <c>BeginTransactionAsync</c>, so commit/rollback isolation is untestable there
/// (the file itself notes this and defers the real cases to integration tests). These tests
/// run against a genuine PostgreSQL container, so <c>Begin</c> → <c>Rollback</c> actually
/// discards writes and <c>Begin</c> → <c>Commit</c> actually persists them.
/// </para>
/// </summary>
public sealed class DbTransactionScopeIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task Dispose_WithoutCommit_RollsBackTheTransaction()
    {
        // Arrange
        await ResetAndMigrateAsync();

        // Act - open a scope, write, then dispose WITHOUT committing. Disposing an
        // uncommitted relational transaction must roll it back (the ambient "unit of work
        // failed" path the services rely on).
        await using (var db = NewContext())
        {
            var sut = new DbTransactionScope(db);
            await sut.BeginTransactionAsync(ct: TestContext.Current.CancellationToken);
            db.Organizations.Add(new Organization { Name = "Leaked?", Slug = "leaked", Description = "never committed" });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            await sut.DisposeAsync();
        }

        // Assert
        await using var verify = NewContext();
        Assert.False(await verify.Organizations.AnyAsync(o => o.Slug == "leaked", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rollback_ThenNewCommittedScope_OnSameContext_PersistsOnlySecondScope()
    {
        // Arrange
        await ResetAndMigrateAsync();
        await using var db = NewContext();

        // Act - first scope rolls back, second scope (reusing the same context) commits.
        // This proves DbTransactionScope cleanly detaches its transaction on rollback so the
        // context can start a fresh, independent transaction afterwards.
        await using (var first = new DbTransactionScope(db))
        {
            await first.BeginTransactionAsync(ct: TestContext.Current.CancellationToken);
            db.Roles.Add(new Role { Name = "Discarded", Description = "rolled back" });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            await first.RollbackAsync(ct: TestContext.Current.CancellationToken);
        }

        await using (var second = new DbTransactionScope(db))
        {
            await second.BeginTransactionAsync(ct: TestContext.Current.CancellationToken);
            db.Roles.Add(new Role { Name = "Persisted", Description = "committed" });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            await second.CommitAsync(ct: TestContext.Current.CancellationToken);
        }

        // Assert
        await using var verify = NewContext();
        Assert.False(await verify.Roles.AnyAsync(r => r.Name == "Discarded", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await verify.Roles.AnyAsync(r => r.Name == "Persisted", cancellationToken: TestContext.Current.CancellationToken));
    }
}
