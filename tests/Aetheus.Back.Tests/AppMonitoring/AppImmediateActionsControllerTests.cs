// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary> R-29: triggering an immediate action needs the right that edits the application (Write on its project), in
/// the organization that owns the project; reading where one stands needs Read. A refusal triggers nothing. </summary>
public sealed class AppImmediateActionsControllerTests
{
    private const int App = 3;
    private const int Project = 5;
    private const int Org = 4;
    private const int OtherOrg = 9;
    private const int RoleInOrg = 11;

    private readonly IAppImmediateActionService _actions = Substitute.For<IAppImmediateActionService>();
    private readonly IAppMonitoringService _monitoring = Substitute.For<IAppMonitoringService>();
    private readonly IPermissionRepository _permissions = Substitute.For<IPermissionRepository>();

    public AppImmediateActionsControllerTests()
    {
        _monitoring.GetAppProjectIdAsync(App, Arg.Any<CancellationToken>()).Returns(Project);
        _actions.ProbeNowAsync(App, Arg.Any<CancellationToken>()).Returns(new AppProbeNowDto { RequestedAt = DateTime.UtcNow });
        _actions.RequestTelemetryExportAsync(App, Arg.Any<CancellationToken>())
            .Returns(new AppTelemetryExportRequestDto { RequestedAt = DateTime.UtcNow });
        _actions.GetProbeNowAsync(App, Arg.Any<CancellationToken>()).Returns(new AppProbeNowDto());
        _permissions.GetRoleIdsForUserAsync("bob", Arg.Any<CancellationToken>()).Returns([]);
        _permissions.GetOrganizationRoleGrantsAsync("bob", Arg.Any<CancellationToken>())
            .Returns([new OrganizationRoleGrant(Org, RoleInOrg, "Project contributor")]);
        _permissions.GetResourceOrganizationIdAsync(ResourceType.Project, Project, Arg.Any<CancellationToken>()).Returns(Org);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Grant(Permission level) => _permissions
        .HasPermissionAsync(Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { RoleInOrg })), ResourceType.Project, Project,
            Arg.Is<Permission>(required => required <= level), Arg.Any<CancellationToken>())
        .Returns(true);

    private AppImmediateActionsController Controller()
    {
        var authz = new ResourceAuthorizationService(_permissions, new MemoryCache(new MemoryCacheOptions()), new AuthzCacheEvictor());
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "bob")], "test"));
        return new AppImmediateActionsController(_actions, _monitoring, authz)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
    }

    [Fact]
    public async Task WriteOnTheProject_TriggersBothActions()
    {
        Grant(Permission.Write);

        Assert.IsType<OkObjectResult>((await Controller().ProbeNow(App, Ct)).Result);
        Assert.IsType<OkObjectResult>((await Controller().RequestTelemetryExport(App, Ct)).Result);
        await _actions.Received(1).ProbeNowAsync(App, Arg.Any<CancellationToken>());
        await _actions.Received(1).RequestTelemetryExportAsync(App, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadOnly_IsRefused_AndTriggersNothing()
    {
        Grant(Permission.Read);

        Assert.IsType<ForbidResult>((await Controller().ProbeNow(App, Ct)).Result);
        Assert.IsType<ForbidResult>((await Controller().RequestTelemetryExport(App, Ct)).Result);
        await _actions.DidNotReceiveWithAnyArgs().ProbeNowAsync(default, Ct);
        await _actions.DidNotReceiveWithAnyArgs().RequestTelemetryExportAsync(default, Ct);
    }

    [Fact]
    public async Task ReadOnly_StillReadsWhereTheLastActionStands()
    {
        Grant(Permission.Read);

        Assert.IsType<OkObjectResult>((await Controller().GetProbeNow(App, Ct)).Result);
    }

    [Fact]
    public async Task WriteInAnotherOrganization_GrantsNothingOnThisApplication()
    {
        Grant(Permission.Admin);
        _permissions.GetResourceOrganizationIdAsync(ResourceType.Project, Project, Arg.Any<CancellationToken>()).Returns(OtherOrg);

        Assert.IsType<ForbidResult>((await Controller().ProbeNow(App, Ct)).Result);
        Assert.IsType<ForbidResult>((await Controller().RequestTelemetryExport(App, Ct)).Result);
        Assert.IsType<ForbidResult>((await Controller().GetTelemetryExportRequest(App, Ct)).Result);
        await _actions.DidNotReceiveWithAnyArgs().ProbeNowAsync(default, Ct);
        await _actions.DidNotReceiveWithAnyArgs().RequestTelemetryExportAsync(default, Ct);
    }

    [Fact]
    public async Task AnUnknownApplication_IsNotFound_BeforeAnyRightIsRead()
    {
        _monitoring.GetAppProjectIdAsync(42, Arg.Any<CancellationToken>()).Returns((int?)null);

        Assert.IsType<NotFoundResult>((await Controller().ProbeNow(42, Ct)).Result);
        await _permissions.DidNotReceiveWithAnyArgs().GetOrganizationRoleGrantsAsync(default!, Ct);
    }
}
