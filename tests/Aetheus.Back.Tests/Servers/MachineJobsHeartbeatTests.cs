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

/// <summary>Recette R-27: the reading of the machine's own jobs and of its time zone goes through the real heartbeat path and
/// is stored per server. A report that carries no completed reading leaves the stored one alone; an unchanged report costs no
/// write.</summary>
public sealed class MachineJobsHeartbeatTests
{
    private readonly IServerRepository _repo = Substitute.For<IServerRepository>();
    private readonly Server _server = new() { Id = 7, Name = "web-1", Hostname = "web-1" };
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
    private readonly IAgentUpdateConfirmationService _confirmation;

    public MachineJobsHeartbeatTests()
    {
        _repo.FindServerAsync(_server.Id, Arg.Any<CancellationToken>()).Returns(_server);
        _confirmation = Substitute.For<IAgentUpdateConfirmationService>();
        _confirmation.ProcessHeartbeatAsync(_server.Id, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
    }

    private Task HeartbeatAsync(MachineJobsDataDto? reading)
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
            _server.Id, new ServerHeartbeatDto { AgentVersion = "1.0.0", MachineJobs = reading }, TestContext.Current.CancellationToken);
    }

    private static MachineJobsDataDto Reading(params MachineJobEntryDto[] jobs) =>
        new() { Collected = true, TimeZoneId = "Europe/Paris", SkippedLines = 3, Jobs = [.. jobs] };

    private static MachineJobEntryDto Job(string cron = "0 2 * * *") => new()
    {
        Source = MachineJobSource.UserCrontab,
        Name = "backup.sh",
        User = "alice",
        CronExpression = cron,
        TimeZoneId = "Europe/Paris",
        Detail = "/var/spool/cron/crontabs/alice"
    };

    [Fact]
    public async Task ACompletedReading_IsStoredOnTheServer_WithTheMachineZone()
    {
        await HeartbeatAsync(Reading(Job()));

        var stored = MachineJobsRecorder.Read(_server.MachineJobsJson);
        Assert.NotNull(stored);
        Assert.True(stored.Collected);
        Assert.Equal("Europe/Paris", stored.TimeZoneId);
        Assert.Equal(3, stored.SkippedLines);
        var job = Assert.Single(stored.Jobs);
        Assert.Equal("0 2 * * *", job.CronExpression);
        Assert.Equal("alice", job.User);
        Assert.Equal(MachineJobSource.UserCrontab, job.Source);
    }

    [Fact]
    public async Task AReadingWithNoJob_IsStored_SoAFormerJob_IsNoLongerShown()
    {
        await HeartbeatAsync(Reading(Job()));

        await HeartbeatAsync(Reading());

        Assert.Empty(MachineJobsRecorder.Read(_server.MachineJobsJson)!.Jobs);
    }

    [Fact]
    public async Task AHeartbeatWithoutAReading_KeepsTheStoredOne()
    {
        await HeartbeatAsync(Reading(Job()));
        var before = _server.MachineJobsJson;

        await HeartbeatAsync(null);

        Assert.Equal(before, _server.MachineJobsJson);
    }

    [Fact]
    public async Task AReadingOfAHostWithNothingToRead_Windows_KeepsTheStoredOne_AndStoresNothingFirst()
    {
        await HeartbeatAsync(new MachineJobsDataDto { Collected = false });
        Assert.Null(_server.MachineJobsJson);

        await HeartbeatAsync(Reading(Job()));
        var before = _server.MachineJobsJson;
        await HeartbeatAsync(new MachineJobsDataDto { Collected = false });

        Assert.Equal(before, _server.MachineJobsJson);
    }

    [Fact]
    public void AnUnchangedReading_CostsNoWrite_AChangedOneDoes()
    {
        var heartbeat = new ServerHeartbeatDto { MachineJobs = Reading(Job()) };

        Assert.True(MachineJobsRecorder.Record(_server, heartbeat));
        Assert.False(MachineJobsRecorder.Record(_server, heartbeat));
        Assert.True(MachineJobsRecorder.Record(_server, new ServerHeartbeatDto { MachineJobs = Reading(Job("30 3 * * *")) }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void AMissingOrUnreadableStoredReading_ReadsAsNothing(string? json)
    {
        Assert.Null(MachineJobsRecorder.Read(json));
    }
}
