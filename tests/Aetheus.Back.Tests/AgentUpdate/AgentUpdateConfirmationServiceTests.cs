// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>A heartbeat that closes an agent update is audited, broadcast to the fleet and server groups and
/// notified: as a confirmation when the new version is running, as a failure otherwise. A heartbeat that
/// concerns no update does none of it.</summary>
public sealed class AgentUpdateConfirmationServiceTests
{
    private const int ServerId = 12;

    private readonly IAgentUpdateRepository _repository = Substitute.For<IAgentUpdateRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IClientProxy _proxy = Substitute.For<IClientProxy>();
    private readonly IUserNotificationService _notifications = Substitute.For<IUserNotificationService>();
    private readonly AgentUpdateConfirmationService _sut;
    private IReadOnlyList<string>? _groups;

    public AgentUpdateConfirmationServiceTests()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Groups(Arg.Do<IReadOnlyList<string>>(groups => _groups = groups)).Returns(_proxy);
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(clients);
        _sut = new AgentUpdateConfirmationService(
            _repository,
            _audit,
            hub,
            NullLogger<AgentUpdateConfirmationService>.Instance,
            new AgentUpdateNotificationPublisher(_notifications, NullLogger<AgentUpdateNotificationPublisher>.Instance));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RepositoryReturns(AgentUpdateRequest? request) =>
        _repository.ConfirmFromHeartbeatAsync(ServerId, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns(request);

    [Fact]
    public async Task ProcessHeartbeatAsync_AConfirmedUpdate_IsAuditedBroadcastAndNotifiedThenReturnsTheVersion()
    {
        RepositoryReturns(new AgentUpdateRequest
        {
            Id = 40,
            ServerId = ServerId,
            Status = AgentUpdateRequestStatus.Confirmed,
            TargetVersion = "2.4.0",
            ConfirmedSessionId = "session-9"
        });

        var version = await _sut.ProcessHeartbeatAsync(ServerId, new ServerHeartbeatDto(), Ct);

        Assert.Equal("2.4.0", version);
        await _audit.Received(1).LogAsync(
            "AgentUpdateConfirmed", "Server", ServerId, "Agent 2.4.0 confirmed by session session-9", Ct);
        Assert.Equal([HubGroups.AllServers, HubGroups.Server(ServerId)], _groups);
        await _proxy.Received(1).SendCoreAsync(
            "AgentUpdateConfirmed",
            Arg.Is<object?[]>(args => args.Length == 2 && (int)args[0]! == ServerId && (int)args[1]! == 40),
            Ct);
        await _notifications.Received(1).RecordServerEventAsync(
            NotificationEventTypes.AgentUpdateCompleted, ServerId, Arg.Any<Func<int, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_AFailedUpdate_IsAuditedWithItsCauseBroadcastAsAFailureAndReturnsNull()
    {
        RepositoryReturns(new AgentUpdateRequest
        {
            Id = 41,
            ServerId = ServerId,
            Status = AgentUpdateRequestStatus.Failed,
            TargetVersion = "2.4.0",
            ObservedVersion = "2.3.0",
            FailureCode = "VersionMismatch",
            FailureDiagnostic = "agent still reports 2.3.0"
        });

        var version = await _sut.ProcessHeartbeatAsync(ServerId, new ServerHeartbeatDto(), Ct);

        Assert.Null(version);
        await _audit.Received(1).LogAsync(
            "AgentUpdateConfirmationFailed", "Server", ServerId, "VersionMismatch: agent still reports 2.3.0", Ct);
        await _proxy.Received(1).SendCoreAsync(
            "AgentUpdateFailed",
            Arg.Is<object?[]>(args => args.Length == 2 && (int)args[1]! == 41),
            Ct);
        await _notifications.Received(1).RecordServerEventAsync(
            NotificationEventTypes.AgentUpdateFailed, ServerId, Arg.Any<Func<int, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_ABrokenNotification_DoesNotFailTheHeartbeat()
    {
        RepositoryReturns(new AgentUpdateRequest
        {
            Id = 42,
            ServerId = ServerId,
            Status = AgentUpdateRequestStatus.Confirmed,
            TargetVersion = "2.4.0"
        });
        _notifications.RecordServerEventAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Func<int, object>>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("mail relay down"));

        var version = await _sut.ProcessHeartbeatAsync(ServerId, new ServerHeartbeatDto(), Ct);

        Assert.Equal("2.4.0", version);
        await _audit.Received(1).LogAsync(
            "AgentUpdateConfirmed", "Server", ServerId, Arg.Any<string?>(), Ct);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_AHeartbeatThatConcernsNoUpdate_DoesNothing()
    {
        RepositoryReturns(null);

        var version = await _sut.ProcessHeartbeatAsync(ServerId, new ServerHeartbeatDto(), Ct);

        Assert.Null(version);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, Ct);
        await _proxy.DidNotReceiveWithAnyArgs().SendCoreAsync(default!, default!, Ct);
        await _notifications.DidNotReceiveWithAnyArgs().RecordServerEventAsync(default!, default, default!, Ct);
    }
}
