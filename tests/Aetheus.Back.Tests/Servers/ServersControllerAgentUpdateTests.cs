// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>An agent reports its self-update progress only for its own server, and "update all"
/// queues updates only on the servers the caller administers.</summary>
public sealed class ServersControllerAgentUpdateTests
{
    private readonly IAgentUpdateService _agentUpdate = Substitute.For<IAgentUpdateService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();

    private ServersController Build(params Claim[] claims) => new(
        Substitute.For<IServerLifecycleService>(), Substitute.For<IServerRetirementService>(),
        Substitute.For<IServerHeartbeatService>(), Substitute.For<IServerServiceManagementService>(),
        Substitute.For<IServerAgentContactService>(), Substitute.For<IServerDiagnosticService>(),
        _agentUpdate, _authz, Substitute.For<IAuthService>(), TimeProvider.System,
        Substitute.For<ILogger<ServersController>>())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        }
    };

    private static readonly AgentUpdateProgressReport Report = new() { Phase = AgentUpdatePhase.PickedUp, Percent = 40, Message = "downloading" };

    [Fact]
    public async Task AProgressReportFromTheServersOwnAgent_IsBroadcastAsIs()
    {
        var result = await Build(new Claim("ServerId", "5")).ReportAgentUpdateProgress(5, Report, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await _agentUpdate.Received(1).BroadcastProgressAsync(
            Arg.Is<AgentUpdateProgressDto>(dto => dto.ServerId == 5 && dto.Phase == AgentUpdatePhase.PickedUp
                && dto.Percent == 40 && dto.Message == "downloading"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("x")]
    [InlineData("6")]
    public async Task AProgressReportForAnotherServer_IsForbiddenAndNotBroadcast(string? serverId)
    {
        var claims = serverId is null ? Array.Empty<Claim>() : [new Claim("ServerId", serverId)];

        var result = await Build(claims).ReportAgentUpdateProgress(5, Report, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _agentUpdate.DidNotReceiveWithAnyArgs().BroadcastProgressAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateOne_NeedsAdminOnThatServer()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 5, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await Build().UpdateAgent(5, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _agentUpdate.DidNotReceiveWithAnyArgs().QueueUpdateAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData(null, "unknown")]
    public async Task UpdateOne_QueuesTheUpdateInTheCallersName(string? name, string requestedBy)
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 5, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        var queued = new AgentUpdateResponse();
        _agentUpdate.QueueUpdateAsync(5, requestedBy, Arg.Any<CancellationToken>()).Returns(queued);
        var claims = name is null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.Name, name)];

        var result = await Build(claims).UpdateAgent(5, TestContext.Current.CancellationToken);

        Assert.Same(queued, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task EnableCapabilities_NeedsAdminOnThatServer()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 5, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await Build().EnableAgentCapabilities(
            5, new AgentEnableCapabilitiesRequest { Capabilities = [AgentEnableableCapabilities.PatchManage] },
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _agentUpdate.DidNotReceiveWithAnyArgs().QueueUpdateWithCapabilitiesAsync(
            default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnableCapabilities_QueuesTheRequestedCapabilitiesInTheCallersName()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 5, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        var queued = new AgentUpdateResponse();
        _agentUpdate.QueueUpdateWithCapabilitiesAsync(
                5, "alice", Arg.Is<IReadOnlyCollection<string>>(list => list.SequenceEqual(new[] { "patch-manage" })),
                Arg.Any<CancellationToken>())
            .Returns(queued);

        var result = await Build(new Claim(ClaimTypes.Name, "alice")).EnableAgentCapabilities(
            5, new AgentEnableCapabilitiesRequest { Capabilities = [AgentEnableableCapabilities.PatchManage] },
            TestContext.Current.CancellationToken);

        Assert.Same(queued, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task UpdateAll_WithNoAdministrableServer_QueuesNothing()
    {
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(new List<int>());

        var result = await Build().UpdateAllAgents(TestContext.Current.CancellationToken);

        Assert.Equal(0, Assert.IsType<AgentUpdateAllResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).QueuedCount);
        await _agentUpdate.DidNotReceiveWithAnyArgs().QueueUpdateAllAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData(null, "unknown")]
    public async Task UpdateAll_QueuesOnTheAdministrableServersInTheCallersName(string? name, string requestedBy)
    {
        List<int> ids = [2, 3];
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(ids);
        var queued = new AgentUpdateAllResponse { QueuedCount = 2, ServerIds = ids };
        _agentUpdate.QueueUpdateAllAsync(ids, requestedBy, Arg.Any<CancellationToken>()).Returns(queued);
        var claims = name is null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.Name, name)];

        var result = await Build(claims).UpdateAllAgents(TestContext.Current.CancellationToken);

        Assert.Same(queued, Assert.IsType<OkObjectResult>(result.Result).Value);
    }
}
