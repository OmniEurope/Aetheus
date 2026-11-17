// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Servers;

/// <summary>R-2: the uptime text, CPU and memory of a stored container are refreshed in place, row by row and
/// only where a value moved, instead of deleting and inserting the whole container list.</summary>
public sealed class ServerHeartbeatContainerLiveFieldsTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ServerHeartbeatRepository _repository;

    public ServerHeartbeatContainerLiveFieldsTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        _repository = new ServerHeartbeatRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyTheLiveFieldsOfTheMatchingContainerMove_AndTheRowKeepsItsId()
    {
        _db.DockerContainers.AddRange(
            new DockerContainer { ServerId = 5, ContainerId = "a", Name = "app", Image = "app:1", Status = "Up 3 minutes", CpuPercent = 0.4, MemoryUsageMb = 100 },
            new DockerContainer { ServerId = 5, ContainerId = "b", Name = "db", Image = "db:1", Status = "Up 3 minutes", CpuPercent = 0.1, MemoryUsageMb = 50 });
        await _db.SaveChangesAsync(Ct);
        var idBefore = (await _db.DockerContainers.AsNoTracking().SingleAsync(c => c.ContainerId == "a", Ct)).Id;

        await _repository.UpdateDockerContainerLiveFieldsAsync(5, [
            new DockerContainer { ContainerId = "a", Status = "Up 4 minutes", CpuPercent = 2.5, MemoryUsageMb = 130 },
            new DockerContainer { ContainerId = "ghost", Status = "Up 1 minute", CpuPercent = 9, MemoryUsageMb = 9 }], Ct);
        await _db.SaveChangesAsync(Ct);

        var rows = await _db.DockerContainers.AsNoTracking().OrderBy(c => c.ContainerId).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(idBefore, rows[0].Id);
        Assert.Equal(("Up 4 minutes", 2.5, 130d), (rows[0].Status, rows[0].CpuPercent, rows[0].MemoryUsageMb));
        Assert.Equal("app:1", rows[0].Image);
        Assert.Equal(("Up 3 minutes", 0.1, 50d), (rows[1].Status, rows[1].CpuPercent, rows[1].MemoryUsageMb));
    }
}
