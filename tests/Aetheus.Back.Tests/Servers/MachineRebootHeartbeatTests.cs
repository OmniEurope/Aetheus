// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Recette R-11: the reading of the machine's own reboots goes through the real heartbeat path and is stored per
/// server. A report that carries no completed reading leaves the stored one alone; an unchanged report costs no write.</summary>
public sealed class MachineRebootHeartbeatTests
{
    private readonly IServerRepository _repo = Substitute.For<IServerRepository>();
    private readonly Server _server = new() { Id = 7, Name = "web-1", Hostname = "web-1" };
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero));

    public MachineRebootHeartbeatTests()
    {
        _repo.FindServerAsync(_server.Id, Arg.Any<CancellationToken>()).Returns(_server);
        var confirmation = Substitute.For<IAgentUpdateConfirmationService>();
        confirmation.ProcessHeartbeatAsync(_server.Id, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _confirmation = confirmation;
    }

    private readonly IAgentUpdateConfirmationService _confirmation;

    private Task HeartbeatAsync(MachineRebootDataDto? reading)
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(Substitute.For<IHubClients>());
        var service = new ServerService(_repo, Substitute.For<IServerHeartbeatRepository>(), hub, alertHub,
            Substitute.For<IAuditService>(), Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), _time,
            Substitute.For<IDbTransactionScope>(), updateConfirmation: _confirmation,
            domainEvents: Substitute.For<IDomainEventDispatcher>());
        return service.ProcessHeartbeatAsync(
            _server.Id, new ServerHeartbeatDto { AgentVersion = "1.0.0", MachineReboots = reading }, TestContext.Current.CancellationToken);
    }

    private static MachineRebootDataDto Reading(params MachineRebootEntryDto[] entries) => new() { Collected = true, Entries = [.. entries] };

    private static MachineRebootEntryDto Daily(string time = "0 2 * * *") => new()
    {
        Source = MachineRebootSource.UnattendedUpgrades,
        CronExpression = time,
        TimeZoneId = "Europe/Paris",
        Conditional = true,
        Detail = "/etc/apt/apt.conf.d/50unattended-upgrades"
    };

    [Fact]
    public async Task ACompletedReading_IsStoredOnTheServer()
    {
        await HeartbeatAsync(Reading(Daily()));

        var stored = MachineRebootRecorder.Read(_server.MachineRebootJson);
        Assert.NotNull(stored);
        Assert.True(stored.Collected);
        var entry = Assert.Single(stored.Entries);
        Assert.Equal("0 2 * * *", entry.CronExpression);
        Assert.Equal("Europe/Paris", entry.TimeZoneId);
        Assert.Equal(MachineRebootSource.UnattendedUpgrades, entry.Source);
        Assert.True(entry.Conditional);
    }

    [Fact]
    public async Task AReadingWithNoEntry_IsStored_SoAFormerReboot_IsNoLongerShown()
    {
        await HeartbeatAsync(Reading(Daily()));

        await HeartbeatAsync(Reading());

        Assert.Empty(MachineRebootRecorder.Read(_server.MachineRebootJson)!.Entries);
    }

    [Fact]
    public async Task AHeartbeatWithoutAReading_KeepsTheStoredOne()
    {
        await HeartbeatAsync(Reading(Daily()));
        var before = _server.MachineRebootJson;

        await HeartbeatAsync(null);

        Assert.Equal(before, _server.MachineRebootJson);
    }

    [Fact]
    public async Task AReadingOfAHostWithNothingToRead_KeepsTheStoredOne_AndStoresNothingFirst()
    {
        await HeartbeatAsync(new MachineRebootDataDto { Collected = false });
        Assert.Null(_server.MachineRebootJson);

        await HeartbeatAsync(Reading(Daily()));
        var before = _server.MachineRebootJson;
        await HeartbeatAsync(new MachineRebootDataDto { Collected = false });

        Assert.Equal(before, _server.MachineRebootJson);
    }

    [Fact]
    public void AnUnchangedReading_CostsNoWrite_AChangedOneDoes()
    {
        var heartbeat = new ServerHeartbeatDto { MachineReboots = Reading(Daily()) };

        Assert.True(MachineRebootRecorder.Record(_server, heartbeat));
        Assert.False(MachineRebootRecorder.Record(_server, heartbeat));
        Assert.True(MachineRebootRecorder.Record(_server, new ServerHeartbeatDto { MachineReboots = Reading(Daily("30 3 * * *")) }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void AMissingOrUnreadableStoredReading_ReadsAsNothing(string? json)
    {
        Assert.Null(MachineRebootRecorder.Read(json));
    }
}
