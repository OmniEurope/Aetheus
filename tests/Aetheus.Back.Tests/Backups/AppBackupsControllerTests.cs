// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Backups;

public sealed class AppBackupsControllerTests
{
    private readonly IBackupPolicyService _service = Substitute.For<IBackupPolicyService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();

    [Fact]
    public async Task GetPolicies_ProjectScope_PassesOnlyRequestedProject()
    {
        var controller = CreateController();
        _authz.GetAccessibleResourceIdsAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 3, 7 });
        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _service.GetPoliciesAsync(
                Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<BackupPolicyDto>());

        var result = await controller.GetPolicies(
            new PaginationRequest(), 7, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        await _service.Received(1).GetPoliciesAsync(
            Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.Count == 1 && ids.Contains(7)),
            Arg.Any<PaginationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPolicies_ProjectScopeWithoutPermission_IsForbidden()
    {
        var controller = CreateController();
        _authz.GetAccessibleResourceIdsAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 3 });
        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await controller.GetPolicies(
            new PaginationRequest(), 7, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().GetPoliciesAsync(
            Arg.Any<IReadOnlyCollection<int>?>(),
            Arg.Any<PaginationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_AgentParametersWithoutServerWrite_IsForbiddenBeforeAnyChange()
    {
        var controller = CreateController();
        var request = new UpdateBackupPolicyRequest { Name = "db", DbHost = "10.0.0.5" };
        _service.GetOwningProjectIdAsync(4, Arg.Any<CancellationToken>()).Returns(1);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _service.GetServerForAgentParameterChangeAsync(4, request, Arg.Any<CancellationToken>()).Returns(2);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 2, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await controller.Update(4, request, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().UpdateAsync(Arg.Any<int>(), Arg.Any<UpdateBackupPolicyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_AgentParametersWithServerWrite_IsApplied()
    {
        var controller = CreateController();
        var request = new UpdateBackupPolicyRequest { Name = "db", DbHost = "10.0.0.5" };
        _service.GetOwningProjectIdAsync(4, Arg.Any<CancellationToken>()).Returns(1);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int>(), Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _service.GetServerForAgentParameterChangeAsync(4, request, Arg.Any<CancellationToken>()).Returns(2);
        _service.UpdateAsync(4, request, Arg.Any<CancellationToken>()).Returns(new BackupPolicyDto());

        var result = await controller.Update(4, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        await _authz.Received(1).HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 2, Permission.Write, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_ApprovedAgentParametersKept_ProjectWriteIsEnough()
    {
        var controller = CreateController();
        var request = new UpdateBackupPolicyRequest { Name = "renamed" };
        _service.GetOwningProjectIdAsync(4, Arg.Any<CancellationToken>()).Returns(1);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _service.GetServerForAgentParameterChangeAsync(4, request, Arg.Any<CancellationToken>()).Returns((int?)null);
        _service.UpdateAsync(4, request, Arg.Any<CancellationToken>()).Returns(new BackupPolicyDto());

        var result = await controller.Update(4, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        await _authz.DidNotReceive().HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Arg.Any<int>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>());
    }

    private AppBackupsController CreateController() => new(_service, _authz)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        }
    };
}
