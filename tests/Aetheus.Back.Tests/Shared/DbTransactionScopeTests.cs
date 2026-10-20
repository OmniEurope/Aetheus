// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

public class DbTransactionScopeTests : IAsyncDisposable
{
    private readonly AppDbContext _db;
    private readonly DbTransactionScope _sut;

    public DbTransactionScopeTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _sut = new DbTransactionScope(_db);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task CommitAsync_WithoutTransaction_DoesNotThrow()
    {
        await _sut.CommitAsync(ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RollbackAsync_WithoutTransaction_DoesNotThrow()
    {
        await _sut.RollbackAsync(ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Dispose_WithoutTransaction_DoesNotThrow()
    {
        _sut.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_WithoutTransaction_DoesNotThrow()
    {
        await _sut.DisposeAsync();
    }
}

// Real transaction semantics are covered by PostgreSQL integration tests.
