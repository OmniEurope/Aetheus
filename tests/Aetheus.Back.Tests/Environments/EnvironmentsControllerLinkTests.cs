// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Environments;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Duplicating an environment needs to read it and to write environments; (un)linking a project
/// server needs write access to the environment and read access to the server's project.</summary>
public sealed class EnvironmentsControllerLinkTests
{
    private readonly IEnvironmentService _service = Substitute.For<IEnvironmentService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly EnvironmentsController _sut;

    public EnvironmentsControllerLinkTests()
    {
        _sut = new EnvironmentsController(_service, _authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test")) }
            }
        };
    }

    private void Allow(ResourceType type, int? id, Permission permission, bool allowed = true) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), type, id, permission, Arg.Any<CancellationToken>()).Returns(allowed);

    // --- Duplicate ---

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Duplicate_WithoutReadOnTheSourceOrWriteOnEnvironments_IsForbidden(bool canRead, bool canWrite)
    {
        Allow(ResourceType.Environment, 2, Permission.Read, canRead);
        Allow(ResourceType.Environment, null, Permission.Write, canWrite);

        var result = await _sut.DuplicateEnvironment(2, new DuplicateEnvironmentRequest { TargetProjectId = 8 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceiveWithAnyArgs().DuplicateEnvironmentAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Duplicate_OfAnUnknownEnvironment_IsNotFound()
    {
        Allow(ResourceType.Environment, 2, Permission.Read);
        Allow(ResourceType.Environment, null, Permission.Write);
        _service.DuplicateEnvironmentAsync(2, 8, Arg.Any<CancellationToken>()).Returns((EnvironmentDto?)null);

        var result = await _sut.DuplicateEnvironment(2, new DuplicateEnvironmentRequest { TargetProjectId = 8 }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Duplicate_PointsAtTheCopyInTheTargetProject()
    {
        Allow(ResourceType.Environment, 2, Permission.Read);
        Allow(ResourceType.Environment, null, Permission.Write);
        var copy = new EnvironmentDto { Id = 9, Name = "Dev (copy)" };
        _service.DuplicateEnvironmentAsync(2, 8, Arg.Any<CancellationToken>()).Returns(copy);

        var result = await _sut.DuplicateEnvironment(2, new DuplicateEnvironmentRequest { TargetProjectId = 8 }, TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(EnvironmentsController.GetEnvironment), created.ActionName);
        Assert.Equal(9, created.RouteValues!["id"]);
        Assert.Same(copy, created.Value);
    }

    // --- Link / unlink ---

    public static TheoryData<bool> Operations => [true, false];

    private Task<IActionResult> LinkOrUnlink(bool link) => link
        ? _sut.LinkProjectServer(2, 30, TestContext.Current.CancellationToken)
        : _sut.UnlinkProjectServer(2, 30, TestContext.Current.CancellationToken);

    private Task<bool> ServiceCall(bool link) => link
        ? _service.LinkProjectServerAsync(2, 30, Arg.Any<CancellationToken>())
        : _service.UnlinkProjectServerAsync(2, 30, Arg.Any<CancellationToken>());

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Link_WithoutWriteOnTheEnvironment_IsForbidden(bool link)
    {
        Allow(ResourceType.Environment, 2, Permission.Write, false);

        Assert.IsType<ForbidResult>(await LinkOrUnlink(link));
        await _service.DidNotReceiveWithAnyArgs().GetProjectServerProjectIdAsync(default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Link_OfAnUnknownProjectServer_IsNotFound(bool link)
    {
        Allow(ResourceType.Environment, 2, Permission.Write);
        _service.GetProjectServerProjectIdAsync(30, Arg.Any<CancellationToken>()).Returns((int?)null);

        Assert.IsType<NotFoundResult>(await LinkOrUnlink(link));
        await _service.DidNotReceiveWithAnyArgs().LinkProjectServerAsync(default, default, TestContext.Current.CancellationToken);
        await _service.DidNotReceiveWithAnyArgs().UnlinkProjectServerAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Link_OfAServerInAProjectTheCallerCannotRead_IsForbidden(bool link)
    {
        Allow(ResourceType.Environment, 2, Permission.Write);
        _service.GetProjectServerProjectIdAsync(30, Arg.Any<CancellationToken>()).Returns(7);
        Allow(ResourceType.Project, 7, Permission.Read, false);

        Assert.IsType<ForbidResult>(await LinkOrUnlink(link));
        await _service.DidNotReceiveWithAnyArgs().LinkProjectServerAsync(default, default, TestContext.Current.CancellationToken);
        await _service.DidNotReceiveWithAnyArgs().UnlinkProjectServerAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true, true, typeof(NoContentResult))]
    [InlineData(true, false, typeof(NotFoundResult))]
    [InlineData(false, true, typeof(NoContentResult))]
    [InlineData(false, false, typeof(NotFoundResult))]
    public async Task Link_Allowed_ReportsWhetherTheServiceChangedTheLink(bool link, bool changed, Type expected)
    {
        Allow(ResourceType.Environment, 2, Permission.Write);
        _service.GetProjectServerProjectIdAsync(30, Arg.Any<CancellationToken>()).Returns(7);
        Allow(ResourceType.Project, 7, Permission.Read);
        ServiceCall(link).Returns(changed);

        Assert.IsType(expected, await LinkOrUnlink(link));
    }
}
