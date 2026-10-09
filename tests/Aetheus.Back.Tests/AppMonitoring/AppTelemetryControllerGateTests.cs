// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>
/// Telemetry is read and managed through the monitored app's PARENT project permissions, not through a
/// permission of its own. Every action must therefore resolve the parent and check it before touching
/// the service - an action that forgets the gate exposes another tenant's telemetry, and one that
/// checks Read where it mutates lets a reader rotate an ingestion key.
/// </summary>
public class AppTelemetryControllerGateTests
{
    private readonly IAppTelemetryService _telemetry = Substitute.For<IAppTelemetryService>();
    private readonly IAppWebAnalyticsService _webAnalytics = Substitute.For<IAppWebAnalyticsService>();
    private readonly IAppWebAnalyticsConfigurationService _webAnalyticsConfig =
        Substitute.For<IAppWebAnalyticsConfigurationService>();
    private readonly IAppMonitoringService _monitoring = Substitute.For<IAppMonitoringService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IAppTelemetrySendIntervalService _sendInterval = Substitute.For<IAppTelemetrySendIntervalService>();

    private AppTelemetryController Build()
    {
        var controller = new AppTelemetryController(_telemetry, _webAnalytics, _webAnalyticsConfig, _monitoring, _authz, _sendInterval)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("test")) }
            }
        };
        return controller;
    }

    private void AppBelongsToProject(int appId, int projectId) =>
        _monitoring.GetAppProjectIdAsync(appId, Arg.Any<CancellationToken>()).Returns(projectId);

    private void Grant(int projectId, Permission permission) =>
        _authz.HasPermissionAsync(
            Arg.Any<ClaimsPrincipal>(), ResourceType.Project, projectId, permission, Arg.Any<CancellationToken>())
            .Returns(true);

    [Fact]
    public async Task UnknownApp_IsNotFound_AndTheServiceIsNeverCalled()
    {
        _monitoring.GetAppProjectIdAsync(404, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await Build().GetMetricNames(404, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
        await _telemetry.DidNotReceive().GetMetricNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadWithoutPermission_IsForbidden_AndTheServiceIsNeverCalled()
    {
        AppBelongsToProject(1, 7); // no Grant: authz returns false by default

        var result = await Build().GetMetricNames(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _telemetry.DidNotReceive().GetMetricNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadWithPermission_ReturnsTheServiceResult()
    {
        AppBelongsToProject(1, 7);
        Grant(7, Permission.Read);
        _telemetry.GetMetricNamesAsync(1, Arg.Any<CancellationToken>()).Returns(["cpu", "mem"]);

        var result = await Build().GetMetricNames(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(["cpu", "mem"], Assert.IsAssignableFrom<List<string>>(ok.Value));
    }

    [Fact]
    public async Task TheSendInterval_IsGatedByTheParentProject_AndReturnedToAReader()
    {
        AppBelongsToProject(1, 7);
        var refused = await Build().GetSendInterval(1, TestContext.Current.CancellationToken);
        Assert.IsType<ForbidResult>(refused.Result);
        await _sendInterval.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        Grant(7, Permission.Read);
        _sendInterval.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AppTelemetrySendIntervalDto { ServerIntervalSeconds = 300, ObservedIntervalSeconds = 60 });
        var result = await Build().GetSendInterval(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(60, Assert.IsType<AppTelemetrySendIntervalDto>(ok.Value).ObservedIntervalSeconds);
    }

    [Fact]
    public async Task GeneratingAnIngestKey_RequiresWrite_NotRead()
    {
        // A reader must not be able to mint a key that lets anything push telemetry into the app.
        AppBelongsToProject(1, 7);
        Grant(7, Permission.Read);

        var result = await Build().GenerateIngestKey(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _telemetry.DidNotReceive().GenerateIngestKeyAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratingAnIngestKey_WithWrite_ReturnsTheKey()
    {
        AppBelongsToProject(1, 7);
        Grant(7, Permission.Write);
        _telemetry.GenerateIngestKeyAsync(1, Arg.Any<CancellationToken>())
            .Returns(new IngestKeyResponse { Key = "k-123" });

        var result = await Build().GenerateIngestKey(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("k-123", Assert.IsType<IngestKeyResponse>(ok.Value).Key);
    }

    [Fact]
    public async Task GeneratingAnIngestKey_WhenTheServiceHasNothingToGive_IsNotFound()
    {
        AppBelongsToProject(1, 7);
        Grant(7, Permission.Write);
        _telemetry.GenerateIngestKeyAsync(1, Arg.Any<CancellationToken>()).Returns((IngestKeyResponse?)null);

        var result = await Build().GenerateIngestKey(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task RevokingAnIngestKey_RequiresWrite()
    {
        AppBelongsToProject(1, 7);
        Grant(7, Permission.Read);

        var result = await Build().RevokeIngestKey(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _telemetry.DidNotReceive().RevokeIngestKeyAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokingAnExistingKey_IsNoContent_AndAMissingOneIsNotFound()
    {
        AppBelongsToProject(1, 7);
        Grant(7, Permission.Write);

        _telemetry.RevokeIngestKeyAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        Assert.IsType<NoContentResult>(await Build().RevokeIngestKey(1, TestContext.Current.CancellationToken));

        _telemetry.RevokeIngestKeyAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        Assert.IsType<NotFoundResult>(await Build().RevokeIngestKey(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThePermissionIsCheckedOnTheParentProject_NotOnTheAppId()
    {
        // The app id and the project id differ on purpose: checking the app id would silently grant
        // access whenever the two happen to collide.
        AppBelongsToProject(1, 7);
        Grant(7, Permission.Read);
        _telemetry.GetMetricNamesAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        await Build().GetMetricNames(1, TestContext.Current.CancellationToken);

        await _authz.Received().HasPermissionAsync(
            Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Read, Arg.Any<CancellationToken>());
        await _authz.DidNotReceive().HasPermissionAsync(
            Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Arg.Any<Permission>(), Arg.Any<CancellationToken>());
    }
}
