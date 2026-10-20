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

/// <summary>The agent identity a heartbeat carries: an unknown OS family heals from the stored description,
/// a new version, session or install time replaces the stored one, and a beat that omits them keeps what
/// the server already knew.</summary>
public sealed class ServerHeartbeatAgentIdentityTests : IDisposable
{
    private const int ServerId = 7;
    private static readonly DateTime InstalledAt = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

    public ServerHeartbeatAgentIdentityTests()
    {
        _db.Servers.Add(new Server
        {
            Id = ServerId,
            Name = "agent-7",
            Hostname = "agent-7",
            OsDescription = "Ubuntu 24.04 LTS",
            AgentVersion = "1.0.0",
            AgentSessionId = "session-1",
            AgentInstalledAt = InstalledAt,
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private async Task<Server> BeatAsync(ServerHeartbeatDto heartbeat)
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
        return await _db.Servers.AsNoTracking().SingleAsync(server => server.Id == ServerId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ANewVersionSessionAndInstallTime_ReplaceTheStoredIdentity_AndTheOsFamilyHeals()
    {
        var server = await BeatAsync(new ServerHeartbeatDto
        {
            AgentVersion = "1.1.0",
            AgentSessionId = "session-2",
            AgentInstalledAt = InstalledAt.AddDays(1),
        });

        Assert.Equal(OsType.Linux, server.OsType);
        Assert.Equal("1.1.0", server.AgentVersion);
        Assert.Equal("session-2", server.AgentSessionId);
        Assert.Equal(InstalledAt.AddDays(1), server.AgentInstalledAt);
    }

    [Fact]
    public async Task ABeatWithoutIdentity_KeepsWhatTheServerKnew()
    {
        var server = await BeatAsync(new ServerHeartbeatDto { AgentVersion = " ", AgentSessionId = null, AgentInstalledAt = null });

        Assert.Equal("1.0.0", server.AgentVersion);
        Assert.Equal("session-1", server.AgentSessionId);
        Assert.Equal(InstalledAt, server.AgentInstalledAt);
    }

    [Fact]
    public async Task ALegacyServerWithoutSession_TakesTheFirstReportedOne()
    {
        var stored = await _db.Servers.SingleAsync(server => server.Id == ServerId, TestContext.Current.CancellationToken);
        stored.AgentSessionId = null;
        stored.OsType = OsType.Windows;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();

        var server = await BeatAsync(new ServerHeartbeatDto { AgentSessionId = "session-9" });

        Assert.Equal("session-9", server.AgentSessionId);
        Assert.Equal(OsType.Windows, server.OsType);
    }
}
