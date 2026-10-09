// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

public sealed class AppMonitoringControllerTests
{
    private readonly IAppMonitoringService _service = Substitute.For<IAppMonitoringService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly AppMonitoringController _controller;

    public AppMonitoringControllerTests()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _controller = new AppMonitoringController(_service, _authz);
        SetUser([new Claim(ClaimTypes.Name, "operator"), new Claim("ServerId", "7")]);
    }

    [Fact]
    public async Task GetAppsForProject_ReadPermission_ReturnsServiceResult()
    {
        _service.GetAppsForProjectAsync(4, Arg.Any<CancellationToken>())
            .Returns([new MonitoredAppDto { Id = 2, ProjectId = 4, Name = "api" }]);

        var action = await _controller.GetAppsForProject(4, TestContext.Current.CancellationToken);

        var result = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Single(Assert.IsType<List<MonitoredAppDto>>(result.Value));
    }

    [Fact]
    public async Task GetAppsForProject_NoPermission_ForbidsBeforeReading()
    {
        Deny(Permission.Read);

        var action = await _controller.GetAppsForProject(4, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(action.Result);
        await _service.DidNotReceive().GetAppsForProjectAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false, typeof(NotFoundResult))]
    [InlineData(true, false, typeof(NotFoundResult))]
    [InlineData(true, true, typeof(OkObjectResult))]
    public async Task GetApp_EnforcesExistencePermissionAndFreshRead(bool projectExists, bool appExists, Type expected)
    {
        _service.GetAppProjectIdAsync(2, Arg.Any<CancellationToken>()).Returns(projectExists ? 4 : null);
        _service.GetAppAsync(2, Arg.Any<CancellationToken>())
            .Returns(appExists ? new MonitoredAppDto { Id = 2, ProjectId = 4, Name = "api" } : null);

        var action = await _controller.GetApp(2, TestContext.Current.CancellationToken);

        Assert.IsType(expected, action.Result);
    }

    [Fact]
    public async Task GetApp_NoProjectReadPermission_ForbidsWithoutLoadingApp()
    {
        _service.GetAppProjectIdAsync(2, Arg.Any<CancellationToken>()).Returns(4);
        Deny(Permission.Read);

        var action = await _controller.GetApp(2, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(action.Result);
        await _service.DidNotReceive().GetAppAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSamples_ExistingAuthorizedApp_UsesRequestedWindow()
    {
        _service.GetAppProjectIdAsync(2, Arg.Any<CancellationToken>()).Returns(4);
        _service.GetSamplesAsync(2, 72, Arg.Any<CancellationToken>()).Returns([new AppHealthSampleDto()]);

        var action = await _controller.GetSamples(2, 72, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Single(Assert.IsType<List<AppHealthSampleDto>>(ok.Value));
    }

    [Fact]
    public async Task CreateApp_WritePermission_ReturnsCreatedRoute()
    {
        var request = new CreateMonitoredAppRequest { Name = "api", ProjectId = 4, ProbeUrl = "https://api.example" };
        _service.CreateAppAsync(4, request, Arg.Any<CancellationToken>())
            .Returns(new MonitoredAppDto { Id = 9, ProjectId = 4, Name = "api" });

        var action = await _controller.CreateApp(4, request, TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(action.Result);
        Assert.Equal(nameof(AppMonitoringController.GetApp), created.ActionName);
        Assert.Equal(9, created.RouteValues!["id"]);
    }

    [Fact]
    public async Task UpdateAndDeleteApp_ApplyWriteAndAdminPermissions()
    {
        _service.GetAppProjectIdAsync(2, Arg.Any<CancellationToken>()).Returns(4);
        var request = new UpdateMonitoredAppRequest { Name = "renamed", ProbeUrl = "https://api.example" };
        _service.UpdateAppAsync(2, request, Arg.Any<CancellationToken>())
            .Returns(new MonitoredAppDto { Id = 2, ProjectId = 4, Name = "renamed" });
        _service.DeleteAppAsync(2, Arg.Any<CancellationToken>()).Returns(true);

        var updated = await _controller.UpdateApp(2, request, TestContext.Current.CancellationToken);
        var deleted = await _controller.DeleteApp(2, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(updated.Result);
        Assert.IsType<NoContentResult>(deleted);
        await _authz.Received().HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 4, Permission.Write, Arg.Any<CancellationToken>());
        await _authz.Received().HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 4, Permission.Admin, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateApp_AgentProbedOnAServerWithoutServerWrite_IsForbiddenBeforeCreation()
    {
        var request = new CreateMonitoredAppRequest { Name = "api", ProjectId = 4, ServerId = 30, ProbeUrl = "http://10.0.0.1/" };
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 30, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var action = await _controller.CreateApp(4, request, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(action.Result);
        await _service.DidNotReceive().CreateAppAsync(Arg.Any<int>(), Arg.Any<CreateMonitoredAppRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateApp_NewProbeTargetWithoutServerWrite_IsForbiddenBeforeUpdate()
    {
        _service.GetAppProjectIdAsync(2, Arg.Any<CancellationToken>()).Returns(4);
        var request = new UpdateMonitoredAppRequest { Name = "api", ServerId = 30, ProbeUrl = "http://10.0.0.1/" };
        _service.GetProbeServerChangeAsync(2, request, Arg.Any<CancellationToken>()).Returns(30);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 30, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var action = await _controller.UpdateApp(2, request, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(action.Result);
        await _service.DidNotReceive().UpdateAppAsync(Arg.Any<int>(), Arg.Any<UpdateMonitoredAppRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSummary_PassesAuthorizationScopeToService()
    {
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([2, 4]);
        _service.GetSummaryAsync(Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { 2, 4 })), Arg.Any<CancellationToken>())
            .Returns(new AppMonitoringSummaryDto { TotalCount = 2 });

        var action = await _controller.GetSummary(TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(action.Result);
    }

    [Fact]
    public async Task GetProbes_ValidAgentClaim_UsesClaimServerId()
    {
        _service.GetProbeConfigsForServerAsync(7, Arg.Any<CancellationToken>())
            .Returns([new AppProbeConfigDto { MonitoredAppId = 2 }]);

        var action = await _controller.GetProbes(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Single(Assert.IsType<List<AppProbeConfigDto>>(ok.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-int")]
    public async Task GetProbes_InvalidAgentClaim_Forbids(string? claim)
    {
        SetUser(claim is null ? [] : [new Claim("ServerId", claim)]);

        var action = await _controller.GetProbes(TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task ReportProbeResults_EmptyBatch_IsNoOp()
    {
        Assert.IsType<OkResult>(await _controller.ReportProbeResults([], TestContext.Current.CancellationToken));
        await _service.DidNotReceive().IngestProbeResultsAsync(Arg.Any<IReadOnlyCollection<AppProbeResultDto>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportProbeResults_OversizedBatch_IsRejectedBeforeOwnershipLookup()
    {
        var items = Enumerable.Range(1, 1001).Select(id => new AppProbeResultDto { MonitoredAppId = id }).ToList();

        Assert.IsType<BadRequestObjectResult>(await _controller.ReportProbeResults(items, TestContext.Current.CancellationToken));
        await _service.DidNotReceive().GetAppServerIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportProbeResults_ForeignApp_ForbidsWholeBatch()
    {
        var items = new List<AppProbeResultDto>
        {
            new() { MonitoredAppId = 2 },
            new() { MonitoredAppId = 3 }
        };
        _service.GetAppServerIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int?> { [2] = 7, [3] = 8 });

        Assert.IsType<ForbidResult>(await _controller.ReportProbeResults(items, TestContext.Current.CancellationToken));
        await _service.DidNotReceive().IngestProbeResultsAsync(Arg.Any<IReadOnlyCollection<AppProbeResultDto>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportProbeResults_OwnedBatch_IngestsOnceAndReportsAppliedCount()
    {
        var items = new List<AppProbeResultDto>
        {
            new() { MonitoredAppId = 2 },
            new() { MonitoredAppId = 2 }
        };
        _service.GetAppServerIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int?> { [2] = 7 });
        _service.IngestProbeResultsAsync(items, Arg.Any<CancellationToken>()).Returns(2);

        var result = await _controller.ReportProbeResults(items, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("2", System.Text.Json.JsonSerializer.Serialize(ok.Value), StringComparison.Ordinal);
        await _service.Received(1).IngestProbeResultsAsync(items, Arg.Any<CancellationToken>());
    }

    private void Deny(Permission permission) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Arg.Any<int?>(), permission, Arg.Any<CancellationToken>())
            .Returns(false);

    private void SetUser(IEnumerable<Claim> claims) =>
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            }
        };
}
