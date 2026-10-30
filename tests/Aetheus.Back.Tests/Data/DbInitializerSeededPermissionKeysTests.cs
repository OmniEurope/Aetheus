// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Shared.Components.Auth;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.DataSeed;

public sealed class DbInitializerSeededPermissionKeysTests
{
    [Fact]
    public async Task SeedAsync_WritesOnlyKeysOfThePermissionCatalogue_AndEveryBuiltInRoleHoldsItsDefaults()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);

        await DbInitializer.SeedAsync(db);

        var grants = await db.RolePermissionGrants.Include(grant => grant.Role).ToListAsync(TestContext.Current.CancellationToken);
        var dead = grants.Select(grant => grant.PermissionKey).Distinct().Where(key => PermissionCatalog.Find(key) is null).ToList();
        Assert.True(dead.Count == 0, $"The seed writes keys the catalogue no longer has: {string.Join(", ", dead)}");

        foreach (var role in await db.Roles.ToListAsync(TestContext.Current.CancellationToken))
        {
            var expected = PermissionCatalog.DefaultKeysOf(role.Name).Where(key => !PermissionCatalog.Get(key).IsLevel).Order().ToList();
            var held = grants.Where(grant => grant.RoleId == role.Id).Select(grant => grant.PermissionKey).Order().ToList();
            Assert.Equal(expected, held);
        }
    }
}
