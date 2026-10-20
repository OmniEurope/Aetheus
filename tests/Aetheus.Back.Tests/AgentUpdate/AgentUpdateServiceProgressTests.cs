// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.AgentUpdate;

/// <summary>An agent's progress report is clamped to 0..100 before it is stored and broadcast to the
/// server's group and to the all-servers group; a report of a failed update also notifies the
/// followers, while any other phase notifies nobody.</summary>
public sealed class AgentUpdateServiceProgressTests
{
    private readonly IClientProxy _serverGroup = Substitute.For<IClientProxy>();
    private readonly IClientProxy _allServers = Substitute.For<IClientProxy>();
    private readonly IAgentUpdateRepository _updateRepo = Substitute.For<IAgentUpdateRepository>();
    private readonly IUserNotificationService _notifications = Substitute.For<IUserNotificationService>();
    private readonly AgentUpdateService _sut;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public AgentUpdateServiceProgressTests()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Group(HubGroups.Server(4)).Returns(_serverGroup);
        clients.Group(HubGroups.AllServers).Returns(_allServers);
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(clients);
        _sut = new AgentUpdateService(
            hub, Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(), Substitute.For<IAuditService>(), _updateRepo,
            Substitute.For<IAgentReleaseCatalog>(), Substitute.For<IAgentCompatibilityPolicy>(),
            NullLogger<AgentUpdateService>.Instance,
            new AgentUpdateNotificationPublisher(_notifications, NullLogger<AgentUpdateNotificationPublisher>.Instance));
    }

    private static AgentUpdateProgressDto Report(AgentUpdatePhase phase, int percent) =>
        new() { ServerId = 4, Phase = phase, Percent = percent };

    private static bool Sent(object?[] arguments, int percent) =>
        arguments is [AgentUpdateProgressDto dto] && dto.Percent == percent && dto.ServerId == 4;

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    [InlineData(60, 60)]
    public async Task AReportedPercent_IsClampedBeforeItIsStoredAndBroadcast(int reported, int expected)
    {
        await _sut.BroadcastProgressAsync(Report(AgentUpdatePhase.Downloading, reported), _ct);

        await _updateRepo.Received(1).MarkProgressAsync(4, Arg.Is<AgentUpdateProgressDto>(dto => dto.Percent == expected), _ct);
        await _serverGroup.Received(1).SendCoreAsync("AgentUpdateProgress", Arg.Is<object?[]>(a => Sent(a, expected)), _ct);
        await _allServers.Received(1).SendCoreAsync("AgentUpdateProgress", Arg.Is<object?[]>(a => Sent(a, expected)), _ct);
    }

    [Fact]
    public async Task AFailedUpdate_NotifiesTheFollowersOfTheServer()
    {
        var request = new AgentUpdateRequest
        {
            Id = 7,
            ServerId = 4,
            Status = AgentUpdateRequestStatus.Failed,
            TargetVersion = "2.0.0",
            FailureDiagnostic = "tar exit code 2"
        };
        _updateRepo.MarkProgressAsync(4, Arg.Any<AgentUpdateProgressDto>(), _ct).Returns(request);

        await _sut.BroadcastProgressAsync(Report(AgentUpdatePhase.Failed, 50), _ct);

        await _notifications.Received(1).RecordServerEventAsync(
            NotificationEventTypes.AgentUpdateFailed, 4, Arg.Any<Func<int, object>>(), _ct);
        await _serverGroup.Received(1).SendCoreAsync("AgentUpdateProgress", Arg.Is<object?[]>(a => Sent(a, 50)), _ct);
    }

    [Fact]
    public async Task AFailedReportWithNoStoredRequest_NotifiesNobody_ButStillBroadcasts()
    {
        _updateRepo.MarkProgressAsync(4, Arg.Any<AgentUpdateProgressDto>(), _ct).Returns((AgentUpdateRequest?)null);

        await _sut.BroadcastProgressAsync(Report(AgentUpdatePhase.Failed, 10), _ct);

        await _notifications.DidNotReceiveWithAnyArgs().RecordServerEventAsync(default!, default, default!, TestContext.Current.CancellationToken);
        await _allServers.Received(1).SendCoreAsync("AgentUpdateProgress", Arg.Is<object?[]>(a => Sent(a, 10)), _ct);
    }

    [Fact]
    public async Task ARunningPhase_NotifiesNobody()
    {
        _updateRepo.MarkProgressAsync(4, Arg.Any<AgentUpdateProgressDto>(), _ct)
            .Returns(new AgentUpdateRequest { Id = 7, ServerId = 4, Status = AgentUpdateRequestStatus.Failed });

        await _sut.BroadcastProgressAsync(Report(AgentUpdatePhase.Extracting, 60), _ct);

        await _notifications.DidNotReceiveWithAnyArgs().RecordServerEventAsync(default!, default, default!, TestContext.Current.CancellationToken);
    }
}
