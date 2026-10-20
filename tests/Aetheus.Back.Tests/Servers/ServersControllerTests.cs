// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ServersControllerTests
{
    private readonly IServerLifecycleService _serviceMock = Substitute.For<IServerLifecycleService>();
    private readonly IServerRetirementService _retirementMock = Substitute.For<IServerRetirementService>();
    private readonly IServerHeartbeatService _heartbeatMock = Substitute.For<IServerHeartbeatService>();
    private readonly IServerServiceManagementService _servicesMock = Substitute.For<IServerServiceManagementService>();
    private readonly IServerAgentContactService _agentContactMock = Substitute.For<IServerAgentContactService>();
    private readonly IServerDiagnosticService _diagnosticMock = Substitute.For<IServerDiagnosticService>();
    private readonly IAgentUpdateService _agentUpdateMock = Substitute.For<IAgentUpdateService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly IAuthService _authServiceMock = Substitute.For<IAuthService>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
    private readonly ServersController _sut;

    public ServersControllerTests()
    {
        _sut = new ServersController(_serviceMock, _retirementMock, _heartbeatMock, _servicesMock,
            _agentContactMock, _diagnosticMock,
            _agentUpdateMock, _authzMock, _authServiceMock, _time,
            Substitute.For<ILogger<ServersController>>());
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim("ServerId", "1")], "test"))
            }
        };
    }

    [Fact]
    public async Task GetServers_NoAccessibleIds_ReturnsEmptyResult()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int>());

        var result = await _sut.GetServers(new PaginationRequest(), ct: TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paginated = Assert.IsType<PaginatedResult<ServerDto>>(ok.Value);
        Assert.Equal(0, paginated.TotalCount);
    }

    [Fact]
    public async Task GetServers_WithAccess_ReturnsData()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Read, Arg.Any<CancellationToken>())
            .Returns((List<int>?)null);
        _serviceMock.GetServersAsync(Arg.Any<PaginationRequest>(), null, null, null, null, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<ServerDto> { Items = [new ServerDto { Id = 1, Name = "srv" }], TotalCount = 1 });

        var result = await _sut.GetServers(new PaginationRequest(), ct: TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paginated = Assert.IsType<PaginatedResult<ServerDto>>(ok.Value);
        Assert.Equal(1, paginated.TotalCount);
    }

    [Fact]
    public async Task GetServerNames_ReturnsOk()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Read, Arg.Any<CancellationToken>())
            .Returns((List<int>?)null);
        _serviceMock.GetServerNamesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(["srv1", "srv2"]);

        var result = await _sut.GetServerNames(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, ((List<string>)ok.Value!).Count);
    }

    [Fact]
    public async Task GetServer_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetServerDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ServerDetailDto { Id = 1, Name = "srv" });

        var result = await _sut.GetServer(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetServer_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetServer(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetServer_NotFound_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetServerDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns((ServerDetailDto?)null);

        var result = await _sut.GetServer(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateServer_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.UpdateServerAsync(1, Arg.Any<UpdateServerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ServerDto { Id = 1, Name = "updated" });

        var result = await _sut.UpdateServer(1, new UpdateServerRequest { Name = "updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateServer_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.UpdateServer(1, new UpdateServerRequest { Name = "x" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdateServer_NotFound_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.UpdateServerAsync(1, Arg.Any<UpdateServerRequest>(), Arg.Any<CancellationToken>())
            .Returns((ServerDto?)null);

        var result = await _sut.UpdateServer(1, new UpdateServerRequest { Name = "x" }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task RetireServer_Authorized_RetiresAndReturnsNoContent()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _retirementMock.RetireServerAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.RetireServer(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await _retirementMock.Received(1).RetireServerAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _retirementMock.DidNotReceive().PurgeServerAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetireServer_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.RetireServer(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _retirementMock.DidNotReceive().RetireServerAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetireServer_NotFound_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _retirementMock.RetireServerAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.RetireServer(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PurgeServer_Forbidden_ReturnsForbidWithoutPurging()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.PurgeServer(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _retirementMock.DidNotReceive().PurgeServerAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeServer_Authorized_ReturnsNoContent_OrNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Arg.Any<int>(), Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _retirementMock.PurgeServerAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _retirementMock.PurgeServerAsync(2, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.IsType<NoContentResult>(await _sut.PurgeServer(1, TestContext.Current.CancellationToken));
        Assert.IsType<NotFoundResult>(await _sut.PurgeServer(2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRetiredServers_ScopesTheListToTheCallerReadableServers()
    {
        var accessible = new List<int> { 4 };
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(accessible);
        _retirementMock.GetRetiredServersAsync(Arg.Any<PaginationRequest>(), accessible, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<RetiredServerDto> { Items = [new RetiredServerDto { Id = 4, Name = "vps2577917" }], TotalCount = 1 });

        var result = await _sut.GetRetiredServers(new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PaginatedResult<RetiredServerDto>>(ok.Value);
        Assert.Equal(4, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Heartbeat_ReturnsOk()
    {
        var result = await _sut.Heartbeat(1, new ServerHeartbeatDto(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        await _heartbeatMock.Received(1).ProcessHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Heartbeat_StampsReceptionBeforeProcessingAndReplyAfterIt()
    {
        // PLAN-010 lot 1: T2 is read before the heartbeat is processed and T3 after, so the 250 ms the
        // processing takes falls between them and is taken out of the agent's round trip.
        var receivedAt = _time.GetUtcNow().UtcDateTime;
        _heartbeatMock.ProcessHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _time.Advance(TimeSpan.FromMilliseconds(250));
                return Task.CompletedTask;
            });
        _authServiceMock.MaybeRenewAgentTokenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ServerHeartbeatResponseDto { RenewedToken = "renewed" });

        var result = await _sut.Heartbeat(1, new ServerHeartbeatDto(), TestContext.Current.CancellationToken);

        var response = Assert.IsType<ServerHeartbeatResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(receivedAt, response.ReceivedAtUtc);
        Assert.Equal(receivedAt.AddMilliseconds(250), response.RepliedAtUtc);
        Assert.Equal("renewed", response.RenewedToken);
    }

    [Fact]
    public async Task GetServerProjects_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetServerProjectsAsync(1, Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<ProjectDto>());

        var result = await _sut.GetServerProjects(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetServerProjects_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetServerProjects(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetServerPipelines_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetServerPipelinesAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _sut.GetServerPipelines(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetServerPipelines_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetServerPipelines(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetServerVariableLibraries_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetServerVariableLibrariesAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _sut.GetServerVariableLibraries(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetServerVariableLibraries_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetServerVariableLibraries(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetServerVaults_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetServerVaultsAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _sut.GetServerVaults(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetServerVaults_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetServerVaults(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetServerTasks_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetServerTasksAsync(1, Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<ServerTaskDto>());

        var result = await _sut.GetServerTasks(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetServerTasks_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetServerTasks(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetServerLogs_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetServerLogsAsync(1, Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<TaskLogDto>());

        var result = await _sut.GetServerLogs(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetServerLogs_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetServerLogs(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task ContactAgent_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _agentContactMock.ContactAgentAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ContactAgentResultDto { Reachable = true });

        var result = await _sut.ContactAgent(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ContactAgentResultDto>(ok.Value);
        Assert.True(dto.Reachable);
    }

    [Fact]
    public async Task ContactAgent_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.ContactAgent(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _agentContactMock.DidNotReceive().ContactAgentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContactAgent_UnknownServer_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _agentContactMock.ContactAgentAsync(1, Arg.Any<CancellationToken>())
            .Returns((ContactAgentResultDto?)null);

        var result = await _sut.ContactAgent(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
