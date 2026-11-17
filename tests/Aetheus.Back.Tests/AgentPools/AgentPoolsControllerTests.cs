// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.AgentPools;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class AgentPoolsControllerTests
{
    private readonly IAgentPoolService _serviceMock = Substitute.For<IAgentPoolService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly AgentPoolsController _sut;

    public AgentPoolsControllerTests()
    {
        _sut = new AgentPoolsController(_serviceMock, _authzMock);
        // R-12: by default the caller may create pools in exactly one organization (5).
        _authzMock.GetOrganizationIdsWithPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 5 });
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    [Fact]
    public async Task GetPools_ReturnsOkWithPaginatedResult()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1 });
        _serviceMock.GetPoolsAsync(Arg.Any<PaginationRequest>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<AgentPoolDto> { Items = [new AgentPoolDto { Id = 1, Name = "Pool" }], TotalCount = 1 });

        var result = await _sut.GetPools(new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PaginatedResult<AgentPoolDto>>(ok.Value);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task GetPool_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetPoolAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AgentPoolDto { Id = 1, Name = "Pool" });

        var result = await _sut.GetPool(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetPool_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetPool(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetPool_NotFound_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetPoolAsync(1, Arg.Any<CancellationToken>())
            .Returns((AgentPoolDto?)null);

        var result = await _sut.GetPool(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreatePool_Authorized_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreatePoolAsync(Arg.Any<CreateAgentPoolRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentPoolDto { Id = 1, Name = "New" });

        var result = await _sut.CreatePool(new CreateAgentPoolRequest { Name = "New" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreatePool_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CreatePool(new CreateAgentPoolRequest { Name = "New" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreatePool_ServerOutsideWritableScope_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 10 });

        var result = await _sut.CreatePool(new CreateAgentPoolRequest
        {
            Name = "New",
            ServerIds = [10, 99]
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().CreatePoolAsync(
            Arg.Any<CreateAgentPoolRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePool_UnrestrictedServerScope_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns((List<int>?)null);
        _serviceMock.CreatePoolAsync(Arg.Any<CreateAgentPoolRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentPoolDto { Id = 1, Name = "New" });

        var result = await _sut.CreatePool(new CreateAgentPoolRequest
        {
            Name = "New",
            ServerIds = [99]
        }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreatePool_WithoutOrganization_GoesToTheCallersOnlyWritableOrganization()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreatePoolAsync(Arg.Any<CreateAgentPoolRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentPoolDto { Id = 1, Name = "New", OrganizationId = 5 });

        var result = await _sut.CreatePool(new CreateAgentPoolRequest { Name = "New" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        await _serviceMock.Received(1).CreatePoolAsync(
            Arg.Is<CreateAgentPoolRequest>(request => request.OrganizationId == 5), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePool_WithoutOrganization_AndSeveralWritable_AsksForIt()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.GetOrganizationIdsWithPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 5, 6 });

        var result = await _sut.CreatePool(new CreateAgentPoolRequest { Name = "New" }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await _serviceMock.DidNotReceive().CreatePoolAsync(Arg.Any<CreateAgentPoolRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePool_InAnOrganizationTheCallerCannotWrite_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.CanCreateInOrganizationAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 9, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CreatePool(new CreateAgentPoolRequest { Name = "New", OrganizationId = 9 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().CreatePoolAsync(Arg.Any<CreateAgentPoolRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePool_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.UpdatePoolAsync(1, Arg.Any<UpdateAgentPoolRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentPoolDto { Id = 1, Name = "Updated" });

        var result = await _sut.UpdatePool(1, new UpdateAgentPoolRequest { Name = "Updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePool_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.UpdatePool(1, new UpdateAgentPoolRequest { Name = "X" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePool_ServerOutsideWritableScope_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 10 });

        var result = await _sut.UpdatePool(1, new UpdateAgentPoolRequest
        {
            Name = "Updated",
            ServerIds = [10, 99]
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().UpdatePoolAsync(
            Arg.Any<int>(), Arg.Any<UpdateAgentPoolRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePool_Authorized_ReturnsNoContent()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.DeletePoolAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeletePool(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeletePool_NotFound_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.DeletePoolAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.DeletePool(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeletePool_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.AgentPool, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.DeletePool(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }
}
