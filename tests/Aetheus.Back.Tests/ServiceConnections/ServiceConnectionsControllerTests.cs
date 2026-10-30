// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.ServiceConnections;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ServiceConnectionsControllerTests
{
    private readonly IServiceConnectionService _serviceMock = Substitute.For<IServiceConnectionService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly ServiceConnectionsController _sut;

    public ServiceConnectionsControllerTests()
    {
        // ADR-059: the caller's organization lets it give a connection to the project.
        _authzMock.CanCreateInProjectAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new ServiceConnectionsController(_serviceMock, _authzMock);
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
    public async Task GetConnections_ReturnsOk()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1 });
        _serviceMock.GetConnectionsAsync(null, Arg.Any<PaginationRequest>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<ServiceConnectionDto> { Items = [new ServiceConnectionDto { Id = 1, Name = "Docker Hub" }], TotalCount = 1 });

        var result = await _sut.GetConnections(null, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetConnection_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetConnectionAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ServiceConnectionDetailDto { Id = 1, Name = "Docker Hub" });

        var result = await _sut.GetConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetConnection_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetConnection_NotFound_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetConnectionAsync(1, Arg.Any<CancellationToken>())
            .Returns((ServiceConnectionDetailDto?)null);

        var result = await _sut.GetConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateConnection_Authorized_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreateConnectionAsync(Arg.Any<CreateServiceConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ServiceConnectionDto { Id = 1, Name = "New" });

        var result = await _sut.CreateConnection(new CreateServiceConnectionRequest { Name = "New", ConfigurationJson = "{}" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreateConnection_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CreateConnection(new CreateServiceConnectionRequest { Name = "X", ConfigurationJson = "{}" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdateConnection_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.UpdateConnectionAsync(1, Arg.Any<UpdateServiceConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ServiceConnectionDto { Id = 1, Name = "Updated" });

        var result = await _sut.UpdateConnection(1, new UpdateServiceConnectionRequest { Name = "Updated", ConfigurationJson = "{}" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DeleteConnection_Authorized_ReturnsNoContent()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.DeleteConnectionAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteConnection_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.DeleteConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task TestConnection_Authorized_ReturnsOk()
    {
        // Testing uses the stored secret, so it is gated on Write (not Read).
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.TestConnectionAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ServiceConnectionTestResultDto { Status = ServiceConnectionTestStatus.Valid });

        var result = await _sut.TestConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task TestConnection_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.TestConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task TestConnection_NotFound_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.ServiceConnection, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.TestConnectionAsync(1, Arg.Any<CancellationToken>()).Returns((ServiceConnectionTestResultDto?)null);

        var result = await _sut.TestConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
