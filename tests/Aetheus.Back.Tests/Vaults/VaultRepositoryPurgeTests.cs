// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Vaults;

public sealed class VaultRepositoryPurgeTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task PurgeHistoricalSecretVersions_DeletesAtMostRequestedVolumeAndKeepsCurrent()
    {
        var now = DateTime.UtcNow;
        _db.VaultSecretVersions.AddRange(
            Enumerable.Range(1, 5).Select(version => new VaultSecretVersion
            {
                VaultSecretId = 7,
                Key = "key",
                Version = version,
                ChangedAt = now.AddDays(-40 - version),
                ChangeType = ChangeType.Updated
            }));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new VaultRepository(_db, TimeProvider.System);

        var firstSweep = await repository.PurgeHistoricalSecretVersionsAsync(
            7,
            now.AddDays(-31),
            maxCount: 2,
            ct: TestContext.Current.CancellationToken);
        var remaining = await _db.VaultSecretVersions.AsNoTracking()
            .Where(version => version.VaultSecretId == 7)
            .OrderBy(version => version.Version)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, firstSweep);
        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, version => version.Version == 5);
    }

    public void Dispose() => _db.Dispose();
}
