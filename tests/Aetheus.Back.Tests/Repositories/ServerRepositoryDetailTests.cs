// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

/// <summary>
/// R-44: the server detail no longer ranks every task, update request and metric sample of the server through filtered
/// Includes (a ROW_NUMBER over the whole partition); each bounded collection is read by its own LIMIT query. These tests
/// cover what that must not change: the same collections, the newest rows only, and only the requested server's rows.
/// They run on EF InMemory, so they prove the loaded content, not the SQL itself nor its timing.
/// </summary>
public sealed class ServerRepositoryDetailTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AppDbContext _db;
    private readonly ServerRepository _repo;

    public ServerRepositoryDetailTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        _repo = new ServerRepository(_db, TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetServerDetailAsync_LoadsTheTenNewestTasks_TheLatestMetricAndTheLatestUpdateRequest_OfThatServerOnly()
    {
        var origin = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var server = new Server { Name = "vps1", Hostname = "vps1.local" };
        var other = new Server { Name = "vps2", Hostname = "vps2.local" };
        _db.Servers.AddRange(server, other);
        await _db.SaveChangesAsync(Ct);

        for (var index = 0; index < 15; index++)
            _db.Tasks.Add(new ServerTask { ServerId = server.Id, Name = $"task-{index}", CreatedAt = origin.AddMinutes(index) });
        _db.Tasks.Add(new ServerTask { ServerId = other.Id, Name = "other-task", CreatedAt = origin.AddDays(1) });
        for (var index = 0; index < 4; index++)
            _db.ServerMetrics.Add(new ServerMetric { ServerId = server.Id, Timestamp = origin.AddMinutes(index), CpuPercent = index });
        _db.ServerMetrics.Add(new ServerMetric { ServerId = other.Id, Timestamp = origin.AddDays(1), CpuPercent = 99 });
        _db.AgentUpdateRequests.Add(new AgentUpdateRequest { ServerId = server.Id, TargetVersion = "1.0.1", RequestedAt = origin });
        _db.AgentUpdateRequests.Add(new AgentUpdateRequest { ServerId = server.Id, TargetVersion = "1.0.2", RequestedAt = origin.AddHours(1) });
        _db.AgentUpdateRequests.Add(new AgentUpdateRequest { ServerId = other.Id, TargetVersion = "9.9.9", RequestedAt = origin.AddDays(2) });
        await _db.SaveChangesAsync(Ct);
        // The context stamps CreatedAt when a row is added; a later save keeps the dates the test chose.
        foreach (var task in _db.Tasks.Local)
            task.CreatedAt = task.Name == "other-task" ? origin.AddDays(1) : origin.AddMinutes(int.Parse(task.Name[5..]));
        await _db.SaveChangesAsync(Ct);

        var detail = await _repo.GetServerDetailAsync(server.Id, Ct);

        Assert.NotNull(detail);
        Assert.Equal(
            Enumerable.Range(5, 10).Reverse().Select(index => $"task-{index}"),
            detail.Tasks.Select(task => task.Name));
        Assert.Equal(3, Assert.Single(detail.Metrics).CpuPercent);
        Assert.Equal("1.0.2", Assert.Single(detail.AgentUpdateRequests).TargetVersion);
    }

    [Fact]
    public async Task GetServerDetailAsync_ForAServerWithNoTaskMetricOrUpdateRequest_ReturnsEmptyCollections()
    {
        var server = new Server { Name = "vps1", Hostname = "vps1.local" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(Ct);

        var detail = await _repo.GetServerDetailAsync(server.Id, Ct);

        Assert.NotNull(detail);
        Assert.Empty(detail.Tasks);
        Assert.Empty(detail.Metrics);
        Assert.Empty(detail.AgentUpdateRequests);
    }

    [Fact]
    public async Task GetServerDetailAsync_ForAnUnknownServer_ReturnsNull() =>
        Assert.Null(await _repo.GetServerDetailAsync(404, Ct));
}
