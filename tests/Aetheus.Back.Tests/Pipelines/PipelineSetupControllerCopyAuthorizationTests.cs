// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Copying a project's pipelines needs read on the source, write on the target, Pipeline/Write
/// and ownership of the target; making the copies also needs library and vault writes.</summary>
public sealed class PipelineSetupControllerCopyAuthorizationTests
{
    private static readonly PipelineProjectCopyRequest Request = new() { SourceProjectId = 1, TargetProjectId = 2 };

    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IPipelineOwnerAuthorization _owner = Substitute.For<IPipelineOwnerAuthorization>();
    private readonly IPipelineProjectCopyService _copy = Substitute.For<IPipelineProjectCopyService>();
    private readonly PipelineSetupController _sut;

    public PipelineSetupControllerCopyAuthorizationTests()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _owner.CanOwnAsync(Arg.Any<ClaimsPrincipal>(), 2, null, null, Arg.Any<CancellationToken>()).Returns(true);
        _sut = new PipelineSetupController(
            Substitute.For<IPipelineSetupReadinessService>(), Substitute.For<IPipelineRequirementsProvisioner>(),
            _copy, _owner, _authz)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("test")) } },
        };
    }

    private void Deny(ResourceType type, int? id, Permission permission) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), type, id, permission, Arg.Any<CancellationToken>()).Returns(false);

    [Fact]
    public async Task APlan_WithEveryRight_IsWorkedOut()
    {
        var result = await _sut.PlanProjectCopy(Request, TestContext.Current.CancellationToken);

        Assert.IsNotType<ForbidResult>(result.Result);
        await _copy.Received(1).PlanAsync(Request, Arg.Any<List<int>?>(), Arg.Any<List<int>?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ResourceType.Project, 1, Permission.Read)]
    [InlineData(ResourceType.Project, 2, Permission.Write)]
    [InlineData(ResourceType.Pipeline, null, Permission.Write)]
    public async Task APlan_WithoutAProjectOrPipelineRight_IsForbidden(ResourceType type, int? id, Permission permission)
    {
        Deny(type, id, permission);

        var result = await _sut.PlanProjectCopy(Request, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _copy.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APlan_WithoutOwningTheTarget_IsForbidden()
    {
        _owner.CanOwnAsync(Arg.Any<ClaimsPrincipal>(), 2, null, null, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.PlanProjectCopy(Request, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task APlan_DoesNotNeedLibraryOrVaultWrites()
    {
        Deny(ResourceType.VariableLibrary, null, Permission.Write);
        Deny(ResourceType.Vault, null, Permission.Write);

        var result = await _sut.PlanProjectCopy(Request, TestContext.Current.CancellationToken);

        Assert.IsNotType<ForbidResult>(result.Result);
    }

    [Theory]
    [InlineData(ResourceType.VariableLibrary)]
    [InlineData(ResourceType.Vault)]
    public async Task TheCopy_WithoutLibraryOrVaultWrite_IsForbidden(ResourceType type)
    {
        Deny(type, null, Permission.Write);

        var result = await _sut.CopyProject(Request, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _copy.DidNotReceiveWithAnyArgs().CopyAsync(default!, default, default, default, default, TestContext.Current.CancellationToken);
    }
}
