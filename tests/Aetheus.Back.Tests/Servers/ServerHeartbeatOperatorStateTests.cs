// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Exercise repository updates that preserve the operator RKHunter schedule and advance
/// security CheckedAt even when report contents are unchanged.</summary>
public sealed class ServerHeartbeatOperatorStateTests : IDisposable
{
    private const int ServerId = 5;
    private const string Schedule = "0 3 * * *";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

    public ServerHeartbeatOperatorStateTests()
    {
        _db.Servers.Add(new Server { Id = ServerId, Name = "sec-5", Hostname = "sec-5" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private async Task BeatAsync(ServerHeartbeatDto heartbeat)
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(Substitute.For<IHubClients>());
        var service = new ServerService(new ServerRepository(_db, _time), new ServerHeartbeatRepository(_db), hub,
            alertHub, Substitute.For<IAuditService>(), Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), _time, Substitute.For<IDbTransactionScope>());
        await service.ProcessHeartbeatAsync(ServerId, heartbeat, TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
    }

    private static ServerHeartbeatDto Report(int rkhunterWarnings = 0, int pendingUpdates = 3) => new()
    {
        Rkhunter = new RkhunterDataDto { IsInstalled = true, Version = "1.4.6", WarningCount = rkhunterWarnings },
        SecurityUpdates = new SecurityUpdatesDataDto
        {
            PackageManagerPresent = true,
            ProbeSucceeded = true,
            PendingTotal = pendingUpdates
        }
    };

    private async Task ScheduleScansAsync(DateTime lastScheduledScanAt)
    {
        var state = await _db.RkhunterStates.SingleAsync(s => s.ServerId == ServerId, TestContext.Current.CancellationToken);
        state.ScanScheduleCron = Schedule;
        state.LastScheduledScanAt = lastScheduledScanAt;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task RewrittenRkhunterSection_KeepsTheScanSchedule()
    {
        await BeatAsync(Report());
        var scheduledAt = _time.GetUtcNow().UtcDateTime;
        await ScheduleScansAsync(scheduledAt);

        _time.Advance(TimeSpan.FromSeconds(30));
        await BeatAsync(Report(rkhunterWarnings: 2));
        _time.Advance(HeartbeatInventoryFingerprints.FullRewriteInterval);
        await BeatAsync(Report(rkhunterWarnings: 2));

        var state = await _db.RkhunterStates.AsNoTracking()
            .SingleAsync(s => s.ServerId == ServerId, TestContext.Current.CancellationToken);
        Assert.Equal(2, state.WarningCount);
        Assert.Equal(Schedule, state.ScanScheduleCron);
        Assert.Equal(scheduledAt, state.LastScheduledScanAt);
    }

    [Fact]
    public async Task RkhunterNoLongerReported_RemovesItsRow()
    {
        await BeatAsync(Report());
        await ScheduleScansAsync(_time.GetUtcNow().UtcDateTime);

        await BeatAsync(Report() with { Rkhunter = new RkhunterDataDto() });

        Assert.False(await _db.RkhunterStates.AnyAsync(s => s.ServerId == ServerId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnchangedSecurityUpdatesReport_StillMovesCheckedAt()
    {
        await BeatAsync(Report());
        var first = await _db.SecurityUpdatesStates.AsNoTracking()
            .SingleAsync(s => s.ServerId == ServerId, TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromSeconds(30));
        await BeatAsync(Report());

        var second = await _db.SecurityUpdatesStates.AsNoTracking()
            .SingleAsync(s => s.ServerId == ServerId, TestContext.Current.CancellationToken);
        Assert.Equal(first.Id, second.Id); // not deleted and reinserted
        Assert.Equal(_time.GetUtcNow().UtcDateTime, second.CheckedAt);
        Assert.Equal(first.CheckedAt.AddSeconds(30), second.CheckedAt);
        Assert.Equal(3, second.PendingTotal);
    }

    [Fact]
    public async Task ChangedSecurityUpdatesReport_IsRewrittenWithTheNewCheckTime()
    {
        await BeatAsync(Report());
        _time.Advance(TimeSpan.FromSeconds(30));

        await BeatAsync(Report(pendingUpdates: 7));

        var state = await _db.SecurityUpdatesStates.AsNoTracking()
            .SingleAsync(s => s.ServerId == ServerId, TestContext.Current.CancellationToken);
        Assert.Equal(7, state.PendingTotal);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, state.CheckedAt);
    }
}
