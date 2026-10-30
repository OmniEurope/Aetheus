// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.PortRegistry;

/// <summary>Allocation requires library Write and server Read because the system records the
/// reservation under the project identity.</summary>
public class PortAllocationControllerTests
{
    private readonly IPortAllocationService _service = Substitute.For<IPortAllocationService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly PortAllocationController _sut;

    public PortAllocationControllerTests()
    {
        _sut = new PortAllocationController(_service, _authz);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
        // The library's project (55) and server 7 share an organization unless a test says otherwise.
        _service.GetLibraryProjectIdAsync(100, Arg.Any<CancellationToken>()).Returns(55);
        _authz.BelongsToProjectOrganizationAsync(55, ResourceType.Server, Arg.Any<IEnumerable<int>?>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Fact]
    public async Task Allocate_OnAServerOfAnotherOrganization_IsForbidden()
    {
        // RBAC audit, finding 4: Server Read held in another organization does not let this library's
        // project reserve ports there.
        Allow(ResourceType.VariableLibrary, 100, Permission.Write);
        Allow(ResourceType.Server, 7, Permission.Read);
        AllowReserve(7);
        _authz.BelongsToProjectOrganizationAsync(55, ResourceType.Server, Arg.Any<IEnumerable<int>?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var allocate = await _sut.Allocate(100, Request, Ct);
        var check = await _sut.Check(100, Request, Ct);

        Assert.IsType<ForbidResult>(allocate.Result);
        Assert.IsType<ForbidResult>(check.Result);
        await _service.DidNotReceive().AllocateIntoLibraryAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Allow(ResourceType type, int id, Permission permission) => _authz
        .HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), type, id, permission, Arg.Any<CancellationToken>())
        .Returns(true);

    private static AllocateLibraryPortsRequest Request => new() { ServerId = 7, Keys = ["PORT_FRONT"] };

    private void AllowReserve(int serverId) => _authz
        .HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.ServerPortsReserve, serverId, Arg.Any<CancellationToken>())
        .Returns(true);

    [Fact]
    public async Task Allocate_WithLibraryWriteAndPortReservation_Proceeds()
    {
        Allow(ResourceType.VariableLibrary, 100, Permission.Write);
        AllowReserve(7);
        _service.AllocateIntoLibraryAsync(100, 7, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([new VariableEntryDto { Key = "PORT_FRONT", Value = "10000" }]);

        var result = await _sut.Allocate(100, Request, Ct);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // R-11 (owner decision): reserving ports is server.ports.reserve, Contributor and above; Server Read
    // alone, what a Reader holds, no longer reserves.
    [Fact]
    public async Task Allocate_WithServerReadButWithoutPortReservation_IsForbidden()
    {
        Allow(ResourceType.VariableLibrary, 100, Permission.Write);
        Allow(ResourceType.Server, 7, Permission.Read);

        var result = await _sut.Allocate(100, Request, Ct);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().AllocateIntoLibraryAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Allocate_WithoutServerWrite_IsStillAllowed()
    {
        // Explicitly asserted: the server's Write level is NOT part of the rule (only the port reservation
        // permission is), and a future tightening that added it would break the deployment owners this
        // feature is for.
        Allow(ResourceType.VariableLibrary, 100, Permission.Write);
        AllowReserve(7);
        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 7, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);
        _service.AllocateIntoLibraryAsync(100, 7, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([new VariableEntryDto { Key = "PORT_FRONT", Value = "10000" }]);

        var result = await _sut.Allocate(100, Request, Ct);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Allocate_WithOnlyLibraryRead_IsForbidden()
    {
        Allow(ResourceType.VariableLibrary, 100, Permission.Read);
        Allow(ResourceType.Server, 7, Permission.Read);

        var result = await _sut.Allocate(100, Request, Ct);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().AllocateIntoLibraryAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Allocate_WithoutPortReservation_IsForbidden()
    {
        Allow(ResourceType.VariableLibrary, 100, Permission.Write);

        var result = await _sut.Allocate(100, Request, Ct);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().AllocateIntoLibraryAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_NeedsOnlyReadOnBothSides()
    {
        Allow(ResourceType.VariableLibrary, 100, Permission.Read);
        Allow(ResourceType.Server, 7, Permission.Read);
        _service.CheckLibraryPortsAsync(100, 7, Arg.Any<CancellationToken>())
            .Returns(new PortCheckResultDto { ServerId = 7, ServerName = "vps" });

        var result = await _sut.Check(100, Request, Ct);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetTargets_WithoutLibraryRead_IsForbidden()
    {
        var result = await _sut.GetTargets(100, Ct);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().GetTargetsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
