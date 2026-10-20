// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class AgentUpdateServiceTests
{
    // The two Server reads are own-reads on the agent-update repository now.
    private readonly IHubContext<ServerHub> _hubMock = Substitute.For<IHubContext<ServerHub>>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IAgentUpdateRepository _updateRepo = Substitute.For<IAgentUpdateRepository>();
    private readonly IAgentReleaseCatalog _releases = Substitute.For<IAgentReleaseCatalog>();
    private readonly IAgentCompatibilityPolicy _compatibility = Substitute.For<IAgentCompatibilityPolicy>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _taskService = Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly AgentUpdateService _sut;

    public AgentUpdateServiceTests()
    {
        var clientProxy = Substitute.For<IClientProxy>();
        var hubClients = Substitute.For<IHubClients>();
        hubClients.Group(Arg.Any<string>()).Returns(clientProxy);
        hubClients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(clientProxy);
        _hubMock.Clients.Returns(hubClients);
        _releases.Current.Returns(new AgentReleaseManifestDto
        {
            SoftwareVersion = "1.0.0",
            ProtocolVersion = 2
        });
        _updateRepo.ReserveAsync(
                Arg.Any<Server>(),
                Arg.Any<AgentReleaseManifestDto>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var server = call.ArgAt<Server>(0);
                return (new AgentUpdateRequest
                {
                    Id = server.Id + 100,
                    ServerId = server.Id,
                    Server = server,
                    TargetVersion = "1.0.0",
                    Status = AgentUpdateRequestStatus.WaitingForIdle,
                    IsActive = true
                }, true);
            });
        _updateRepo.TryQueueAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requestId = call.ArgAt<int>(0);
                var serverId = requestId - 100;
                var server = new Server { Id = serverId, Name = $"srv-{serverId}" };
                var request = new AgentUpdateRequest
                {
                    Id = requestId,
                    ServerId = serverId,
                    Server = server,
                    TargetVersion = "1.0.0",
                    Status = AgentUpdateRequestStatus.Queued,
                    IsActive = true
                };
                var task = new ServerTask
                {
                    Id = requestId + 100,
                    ServerId = serverId,
                    Operation = OperationKind.AgentSelfUpdate
                };
                return (request, task, 0);
            });
        _compatibility.Evaluate(Arg.Any<ServerDto>())
            .Returns(new AgentCompatibilityDto
            {
                Status = AgentCompatibilityStatus.UpdateRecommended
            });

        _sut = new AgentUpdateService(
            _hubMock, _taskService, _audit, _updateRepo, _releases, _compatibility,
            NullLogger<AgentUpdateService>.Instance,
            new AgentUpdateNotificationPublisher(
                Substitute.For<Aetheus.Back.Components.Notifications.IUserNotificationService>(),
                NullLogger<AgentUpdateNotificationPublisher>.Instance));
    }

    [Fact]
    public async Task QueueUpdateAsync_ServerExists_QueuesTaskAndAudits()
    {
        // Item #7: QueueUpdateAsync now resolves the server via FindServerAsync (it needs the
        // name for the TaskQueued broadcast). Setup must return a Server entity, not just a bool.
        _updateRepo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Name = "srv-1", Hostname = "h" });
        var result = await _sut.QueueUpdateAsync(
            1, "admin", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(101, result.RequestId);
        Assert.Equal(201, result.TaskId);
        await _updateRepo.Received(1).ReserveAsync(
            Arg.Is<Server>(server => server.Id == 1),
            Arg.Any<AgentReleaseManifestDto>(),
            "admin",
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(Arg.Any<string>(), "Server", 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueUpdateAsync_ServerNotFound_ThrowsNotFound()
    {
        _updateRepo.FindServerAsync(99, Arg.Any<CancellationToken>()).Returns((Server?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.QueueUpdateAsync(
            99, "admin", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueUpdateAsync_AlreadyUpToDate_StillQueuesTheUpdate()
    {
        // Explicit per-server updates permit reinstalling a current agent for repair, while fleet
        // updates skip current versions.
        _updateRepo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Name = "srv-1", AgentVersion = "1.0.0" });
        _compatibility.Evaluate(Arg.Any<ServerDto>())
            .Returns(new AgentCompatibilityDto { Status = AgentCompatibilityStatus.UpToDate });

        var result = await _sut.QueueUpdateAsync(
            1, "admin", TestContext.Current.CancellationToken);

        Assert.NotEqual(AgentUpdateQueueOutcome.AlreadyUpToDate, result.Outcome);
        await _updateRepo.Received(1).ReserveAsync(
            Arg.Is<Server>(server => server.Id == 1),
            Arg.Any<AgentReleaseManifestDto>(),
            "admin",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueUpdateAllAsync_EmptyList_ReturnsEmptyResponse()
    {
        var result = await _sut.QueueUpdateAllAsync(
            [], "admin", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.QueuedCount);
    }

    [Fact]
    public async Task QueueUpdateAllAsync_WithServers_QueuesAll()
    {
        _updateRepo.GetServersForAgentUpdateAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new Server { Id = 1, Name = "srv-1", Status = ServerStatus.Online, AgentVersion = "0.9.0" },
                new Server { Id = 2, Name = "srv-2", Status = ServerStatus.Online, AgentVersion = "0.9.0" }
            ]);

        var result = await _sut.QueueUpdateAllAsync(
            [1, 2], "admin", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.QueuedCount);
        Assert.Equal(2, result.Results.Count);
        await _updateRepo.DidNotReceive().FindServerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueUpdateAllAsync_NullList_PassesNull()
    {
        _updateRepo.GetServersForAgentUpdateAsync(null, Arg.Any<CancellationToken>())
            .Returns([new Server { Id = 1, Name = "srv-1", Status = ServerStatus.Online, AgentVersion = "0.9.0" }]);

        var result = await _sut.QueueUpdateAllAsync(
            null, "admin", TestContext.Current.CancellationToken);

        Assert.Equal(1, result.QueuedCount);
    }

    [Fact]
    public async Task QueueUpdateAllAsync_UsesCentralCompatibilityToSkipUpToDateServer()
    {
        _updateRepo.GetServersForAgentUpdateAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns([new Server
            {
                Id = 1,
                Name = "srv-1",
                Status = ServerStatus.Online,
                AgentVersion = "1.0.0",
                AgentProtocolVersion = 2,
                LastHeartbeat = DateTime.UtcNow
            }]);
        _compatibility.Evaluate(Arg.Any<ServerDto>())
            .Returns(new AgentCompatibilityDto { Status = AgentCompatibilityStatus.UpToDate });

        var result = await _sut.QueueUpdateAllAsync(
            [1], "admin", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.QueuedCount);
        Assert.Equal(1, result.AlreadyUpToDateCount);
        await _updateRepo.DidNotReceive().ReserveAsync(
            Arg.Any<Server>(),
            Arg.Any<AgentReleaseManifestDto>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewUpdateAllAsync_ClassifiesFleetBeforeMutation()
    {
        _updateRepo.GetServersForCompatibilityAsync(
                Arg.Any<List<int>?>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new ServerDto { Id = 1, Status = ServerStatus.Online },
                new ServerDto { Id = 2, Status = ServerStatus.Online },
                new ServerDto { Id = 3, Status = ServerStatus.Offline },
                new ServerDto { Id = 4, Status = ServerStatus.Online }
            ]);
        _compatibility.Evaluate(Arg.Is<ServerDto>(server => server.Id == 1))
            .Returns(new AgentCompatibilityDto { Status = AgentCompatibilityStatus.UpToDate });
        _compatibility.Evaluate(Arg.Is<ServerDto>(server => server.Id == 2))
            .Returns(new AgentCompatibilityDto { Status = AgentCompatibilityStatus.UpdateRequired });
        _compatibility.Evaluate(Arg.Is<ServerDto>(server => server.Id == 4))
            .Returns(new AgentCompatibilityDto { Status = AgentCompatibilityStatus.UpdateRecommended });
        // A360-17: the preview asks for the whole fleet's active-task counts in ONE round trip now.
        // Servers with no active task are simply absent from the dictionary, which is what "not busy"
        // means here; the previous per-server COUNT made this cost grow with the fleet.
        _updateRepo.CountActiveNonUpdateTasksAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [2] = 2 });

        var result = await _sut.PreviewUpdateAllAsync(
            [1, 2, 3, 4],
            TestContext.Current.CancellationToken);

        Assert.Equal("1.0.0", result.TargetVersion);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal(1, result.AlreadyUpToDateCount);
        Assert.Equal(1, result.OfflineCount);
        Assert.Equal(1, result.IncompatibleCount);
        Assert.Equal(1, result.BusyCount);
        await _updateRepo.DidNotReceive().ReserveAsync(
            Arg.Any<Server>(),
            Arg.Any<AgentReleaseManifestDto>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
}
