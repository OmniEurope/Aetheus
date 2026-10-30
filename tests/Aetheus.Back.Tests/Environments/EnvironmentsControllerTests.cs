// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Environments;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class EnvironmentsControllerTests
{
    private readonly IEnvironmentService _serviceMock = Substitute.For<IEnvironmentService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly EnvironmentsController _sut;

    public EnvironmentsControllerTests()
    {
        // ADR-059: the caller's organization lets it give an environment to the project.
        _authzMock.BelongsToProjectOrganizationAsync(Arg.Any<int?>(), Arg.Any<ResourceType>(), Arg.Any<IEnumerable<int>?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.CanCreateInProjectAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new EnvironmentsController(_serviceMock, _authzMock);
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
    public async Task GetEnvironments_ReturnsOk()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1 });
        _serviceMock.GetEnvironmentsAsync(null, Arg.Any<PaginationRequest>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<EnvironmentDto> { Items = [new EnvironmentDto { Id = 1, Name = "Dev" }], TotalCount = 1 });

        var result = await _sut.GetEnvironments(null, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetEnvironment_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetEnvironmentAsync(1, Arg.Any<CancellationToken>())
            .Returns(new EnvironmentDto { Id = 1, Name = "Dev" });

        var result = await _sut.GetEnvironment(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetEnvironment_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetEnvironment(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetEnvironment_NotFound_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetEnvironmentAsync(1, Arg.Any<CancellationToken>())
            .Returns((EnvironmentDto?)null);

        var result = await _sut.GetEnvironment(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateEnvironment_Authorized_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreateEnvironmentAsync(Arg.Any<CreateEnvironmentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new EnvironmentDto { Id = 1, Name = "New" });

        var result = await _sut.CreateEnvironment(new CreateEnvironmentRequest { Name = "New" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreateEnvironment_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CreateEnvironment(new CreateEnvironmentRequest { Name = "X" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateEnvironment_ServerOutsideWritableScope_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 10 });

        var result = await _sut.CreateEnvironment(new CreateEnvironmentRequest
        {
            Name = "New",
            ServerIds = [10, 99]
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().CreateEnvironmentAsync(
            Arg.Any<CreateEnvironmentRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEnvironment_SourceOutsideReadableScope_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 77, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CreateEnvironment(new CreateEnvironmentRequest
        {
            Name = "Copied",
            SourceEnvironmentId = 77
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().CreateEnvironmentAsync(
            Arg.Any<CreateEnvironmentRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEnvironment_ReadableSource_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 77, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreateEnvironmentAsync(
                Arg.Is<CreateEnvironmentRequest>(request => request.SourceEnvironmentId == 77),
                Arg.Any<CancellationToken>())
            .Returns(new EnvironmentDto { Id = 90, Name = "Copied" });

        var result = await _sut.CreateEnvironment(new CreateEnvironmentRequest
        {
            Name = "Copied",
            SourceEnvironmentId = 77
        }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task UpdateEnvironment_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.UpdateEnvironmentAsync(1, Arg.Any<UpdateEnvironmentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new EnvironmentDto { Id = 1, Name = "Updated" });

        var result = await _sut.UpdateEnvironment(1, new UpdateEnvironmentRequest { Name = "Updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateEnvironment_ServerOutsideWritableScope_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 10 });

        var result = await _sut.UpdateEnvironment(1, new UpdateEnvironmentRequest
        {
            Name = "Updated",
            ServerIds = [10, 99]
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().UpdateEnvironmentAsync(
            Arg.Any<int>(), Arg.Any<UpdateEnvironmentRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteEnvironment_Authorized_ReturnsNoContent()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.DeleteEnvironmentAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteEnvironment(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteEnvironment_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.DeleteEnvironment(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }
}
