// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentPools;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

public class AgentPoolRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AgentPoolRepository _repo;

    public AgentPoolRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new AgentPoolRepository(_db);
    }

    [Fact]
    public async Task GetPoolsPagedAsync_ReturnsPagedResults()
    {
        _db.AgentPools.AddRange(
            new AgentPool { Name = "Pool-A" },
            new AgentPool { Name = "Pool-B" },
            new AgentPool { Name = "Pool-C" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetPoolsPagedAsync(null, 1, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(2, items.Count);
        Assert.Equal("Pool-A", items[0].Name);
    }

    [Fact]
    public async Task GetPoolsPagedAsync_WithSearch_Filters()
    {
        _db.AgentPools.AddRange(
            new AgentPool { Name = "Build Pool" },
            new AgentPool { Name = "Deploy Pool" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetPoolsPagedAsync("Build", 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
        Assert.Equal("Build Pool", items[0].Name);
    }

    [Fact]
    public async Task GetPoolsPagedAsync_WithAccessibleIds_Filters()
    {
        _db.AgentPools.AddRange(
            new AgentPool { Name = "Pool-A" },
            new AgentPool { Name = "Pool-B" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var poolA = await _db.AgentPools.FirstAsync(p => p.Name == "Pool-A", cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetPoolsPagedAsync(null, 1, 10, [poolA.Id], ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
        Assert.Equal("Pool-A", items[0].Name);
    }

    [Fact]
    public async Task GetPoolWithServersAsync_Found_IncludesServers()
    {
        var pool = new AgentPool { Name = "Pool" };
        _db.AgentPools.Add(pool);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var server = new Server { Name = "srv", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.AgentPoolServers.Add(new AgentPoolServer { AgentPoolId = pool.Id, ServerId = server.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetPoolWithServersAsync(pool.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Single(result.Servers);
    }

    [Fact]
    public async Task GetPoolWithServersAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.GetPoolWithServersAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindPoolAsync_Found()
    {
        var pool = new AgentPool { Name = "Pool" };
        _db.AgentPools.Add(pool);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindPoolAsync(pool.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("Pool", result.Name);
    }

    [Fact]
    public async Task FindByNameAsync_Found()
    {
        _db.AgentPools.Add(new AgentPool { Name = "Unique" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindByNameAsync("Unique", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindByNameAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.FindByNameAsync("Missing", ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task AddPoolAsync_Persists()
    {
        await _repo.AddPoolAsync(new AgentPool { Name = "New" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.AgentPools.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovePoolAsync_Removes()
    {
        var pool = new AgentPool { Name = "Del" };
        _db.AgentPools.Add(pool);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RemovePoolAsync(pool, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, await _db.AgentPools.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveChangesAsync_PersistsPendingChanges()
    {
        _db.AgentPools.Add(new AgentPool { Name = "Pending" });
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.AgentPools.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AllServersInOrganizationAsync_IsFalse_WhenOneServerBelongsToAnotherOrganization()
    {
        var own = new Server { Name = "own", Hostname = "own", OrganizationId = 1 };
        var foreign = new Server { Name = "foreign", Hostname = "foreign", OrganizationId = 2 };
        _db.Servers.AddRange(own, foreign);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(await _repo.AllServersInOrganizationAsync(1, [own.Id, own.Id], TestContext.Current.CancellationToken));
        Assert.True(await _repo.AllServersInOrganizationAsync(1, [], TestContext.Current.CancellationToken));
        Assert.False(await _repo.AllServersInOrganizationAsync(1, [own.Id, foreign.Id], TestContext.Current.CancellationToken));
        Assert.False(await _repo.AllServersInOrganizationAsync(1, [own.Id, 999], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NameTakenAsync_IgnoresThePoolItself()
    {
        var pool = new AgentPool { Name = "Taken", OrganizationId = 1 };
        _db.AgentPools.Add(pool);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(await _repo.NameTakenAsync("Taken", null, TestContext.Current.CancellationToken));
        Assert.False(await _repo.NameTakenAsync("Taken", pool.Id, TestContext.Current.CancellationToken));
        Assert.False(await _repo.NameTakenAsync("Free", null, TestContext.Current.CancellationToken));
    }

    public void Dispose() => _db.Dispose();
}
