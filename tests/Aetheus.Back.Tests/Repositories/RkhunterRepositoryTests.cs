// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Rkhunter;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

public class RkhunterRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly RkhunterRepository _repo;
    private readonly int _serverId;

    public RkhunterRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new RkhunterRepository(_db, TimeProvider.System);

        var server = new Server { Name = "S1", Hostname = "h1", Status = ServerStatus.Online };
        _db.Servers.Add(server);
        _db.SaveChanges();
        _serverId = server.Id;
    }

    // --- GetStateAsync ---

    [Fact]
    public async Task GetStateAsync_NotExists_ReturnsNull()
    {
        var result = await _repo.GetStateAsync(_serverId, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetStateAsync_Exists_ReturnsState()
    {
        _db.RkhunterStates.Add(new RkhunterState
        {
            ServerId = _serverId,
            Version = "1.4.6",
            LastScanStatus = "clean",
            WarningCount = 0
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetStateAsync(_serverId, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("1.4.6", result.Version);
    }

    // --- Scan History ---

    [Fact]
    public async Task GetScanHistoryAsync_Empty_ReturnsEmptyList()
    {
        var result = await _repo.GetScanHistoryAsync(_serverId, ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task AddScanResultAsync_Persists()
    {
        var scan = new RkhunterScanResult
        {
            ServerId = _serverId,
            ScanTime = DateTime.UtcNow,
            Status = "clean",
            WarningCount = 0,
            Summary = "All checks passed"
        };

        var result = await _repo.AddScanResultAsync(scan, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Id > 0);
        Assert.Equal(1, await _db.RkhunterScanResults.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetScanHistoryAsync_ReturnsOrderedByDescending()
    {
        _db.RkhunterScanResults.AddRange(
            new RkhunterScanResult { ServerId = _serverId, ScanTime = DateTime.UtcNow.AddHours(-2), Status = "clean", WarningCount = 0, Summary = "Old" },
            new RkhunterScanResult { ServerId = _serverId, ScanTime = DateTime.UtcNow, Status = "warning", WarningCount = 3, Summary = "New" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetScanHistoryAsync(_serverId, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("New", result[0].Summary);
    }

    [Fact]
    public async Task GetScanHistoryAsync_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            _db.RkhunterScanResults.Add(new RkhunterScanResult
            {
                ServerId = _serverId,
                ScanTime = DateTime.UtcNow.AddHours(-i),
                Status = "clean",
                WarningCount = 0,
                Summary = $"Scan {i}"
            });
        }
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetScanHistoryAsync(_serverId, limit: 3, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
    }

    // --- Schedule ---

    [Fact]
    public async Task UpdateScanScheduleAsync_SetsSchedule()
    {
        _db.RkhunterStates.Add(new RkhunterState
        {
            ServerId = _serverId,
            Version = "1.4.6",
            LastScanStatus = "clean"
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var stored = await _repo.UpdateScanScheduleAsync(_serverId, "0 3 * * 0", ct: TestContext.Current.CancellationToken);

        Assert.True(stored);
        var state = await _db.RkhunterStates.FirstAsync(r => r.ServerId == _serverId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("0 3 * * 0", state.ScanScheduleCron);
    }

    [Fact]
    public async Task UpdateScanScheduleAsync_ServerWithoutRkhunterState_StoresNothingAndSaysSo()
    {
        var stored = await _repo.UpdateScanScheduleAsync(_serverId, "0 3 * * 0", ct: TestContext.Current.CancellationToken);

        Assert.False(stored);
        Assert.False(await _db.RkhunterStates.AnyAsync(r => r.ServerId == _serverId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateScanScheduleAsync_ClearsSchedule()
    {
        _db.RkhunterStates.Add(new RkhunterState
        {
            ServerId = _serverId,
            Version = "1.4.6",
            LastScanStatus = "clean",
            ScanScheduleCron = "0 3 * * 0"
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.UpdateScanScheduleAsync(_serverId, null, ct: TestContext.Current.CancellationToken);

        var state = await _db.RkhunterStates.FirstAsync(r => r.ServerId == _serverId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(state.ScanScheduleCron);
    }

    [Fact]
    public async Task GetScheduledStatesAsync_ReturnsOnlyScheduled()
    {
        _db.RkhunterStates.AddRange(
            new RkhunterState { ServerId = _serverId, Version = "1.4.6", LastScanStatus = "clean", ScanScheduleCron = "0 3 * * 0" }
        );

        var otherServer = new Server { Name = "S2", Hostname = "h2", Status = ServerStatus.Online };
        _db.Servers.Add(otherServer);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.RkhunterStates.Add(new RkhunterState { ServerId = otherServer.Id, Version = "1.4.6", LastScanStatus = "clean" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetScheduledStatesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(_serverId, result[0].ServerId);
    }

    [Fact]
    public async Task UpdateLastScheduledScanAsync_SetsTimestamp()
    {
        _db.RkhunterStates.Add(new RkhunterState
        {
            ServerId = _serverId,
            Version = "1.4.6",
            LastScanStatus = "clean"
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.UpdateLastScheduledScanAsync(_serverId, ct: TestContext.Current.CancellationToken);

        var state = await _db.RkhunterStates.FirstAsync(r => r.ServerId == _serverId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(state.LastScheduledScanAt);
        Assert.True(state.LastScheduledScanAt.Value > DateTime.UtcNow.AddMinutes(-1));
    }

    // --- AddTaskAsync ---

    [Fact]
    public async Task AddTaskAsync_Persists()
    {
        await _repo.AddTaskAsync(new ServerTask
        {
            ServerId = _serverId,
            Name = "rkhunter_scan",
            Command = "rkhunter --check",
            Status = TaskExecutionStatus.Pending
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, await _db.Tasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    public void Dispose() => _db.Dispose();
}
