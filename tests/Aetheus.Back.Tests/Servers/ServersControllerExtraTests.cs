// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ServersControllerExtraTests
{
    private readonly IServerLifecycleService _lifecycle = Substitute.For<IServerLifecycleService>();
    private readonly IServerServiceManagementService _services = Substitute.For<IServerServiceManagementService>();
    private readonly IServerDiagnosticService _diagnostic = Substitute.For<IServerDiagnosticService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly ServersController _sut;

    public ServersControllerExtraTests()
    {
        _sut = new ServersController(_lifecycle, Substitute.For<IServerRetirementService>(),
            Substitute.For<IServerHeartbeatService>(), _services,
            Substitute.For<IServerAgentContactService>(), _diagnostic,
            Substitute.For<IAgentUpdateService>(), _authz, Substitute.For<IAuthService>(), TimeProvider.System,
            Substitute.For<ILogger<ServersController>>());
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    private void Allow(bool value)
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int>(),
            Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(value);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<CancellationToken>()).Returns(value);
    }

    [Fact]
    public async Task SetPipelineRunner_Forbidden_ReturnsForbid()
    {
        Allow(false);
        Assert.IsType<ForbidResult>((await _sut.SetPipelineRunner(1, new UpdatePipelineRunnerRequest { Enabled = true }, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task SetPipelineRunner_Authorized_ReturnsOk()
    {
        Allow(true);
        _lifecycle.SetPipelineRunnerEnabledAsync(1, true, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ServerDto());

        Assert.IsType<OkObjectResult>((await _sut.SetPipelineRunner(1, new UpdatePipelineRunnerRequest { Enabled = true }, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task SetPipelineRunner_ServiceNull_ReturnsNotFound()
    {
        Allow(true);
        _lifecycle.SetPipelineRunnerEnabledAsync(1, true, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ServerDto?)null);

        Assert.IsType<NotFoundResult>((await _sut.SetPipelineRunner(1, new UpdatePipelineRunnerRequest { Enabled = true }, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task SetContainerIsolationPolicy_Authorized_ReturnsOk()
    {
        Allow(true);
        _lifecycle.SetContainerIsolationRequiredAsync(1, true, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ServerDto());

        Assert.IsType<OkObjectResult>((await _sut.SetContainerIsolationPolicy(1, new UpdateContainerIsolationPolicyRequest { Required = true }, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetDiagnostic_Authorized_ReturnsOk()
    {
        Allow(true);
        _diagnostic.DiagnoseAsync(1, Arg.Any<CancellationToken>()).Returns(new ServerDiagnosticDto());

        Assert.IsType<OkObjectResult>((await _sut.GetDiagnostic(1, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetDiagnostic_NotFound_ReturnsNotFound()
    {
        Allow(true);
        _diagnostic.DiagnoseAsync(1, Arg.Any<CancellationToken>()).Returns((ServerDiagnosticDto?)null);

        Assert.IsType<NotFoundResult>((await _sut.GetDiagnostic(1, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task ExecuteServiceAction_Forbidden_ReturnsForbid()
    {
        Allow(false);
        Assert.IsType<ForbidResult>(await _sut.ExecuteServiceAction(1, new ServiceActionRequest { ServiceName = "nginx", Action = ServiceAction.Start }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteServiceAction_Authorized_ReturnsOk()
    {
        Allow(true);
        Assert.IsType<OkObjectResult>(await _sut.ExecuteServiceAction(1, new ServiceActionRequest { ServiceName = "nginx", Action = ServiceAction.Start }, TestContext.Current.CancellationToken));
        await _services.Received(1).ExecuteServiceActionAsync(1, Arg.Any<ServiceActionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallService_Authorized_ReturnsOk()
    {
        Allow(true);
        Assert.IsType<OkObjectResult>(await _sut.InstallService(1, new ServiceInstallRequest { ServiceName = "nginx" }, TestContext.Current.CancellationToken));
        await _services.Received(1).InstallServiceAsync(1, "nginx", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UninstallService_Authorized_ReturnsOk()
    {
        Allow(true);
        Assert.IsType<OkObjectResult>(await _sut.UninstallService(1, new ServiceInstallRequest { ServiceName = "nginx" }, TestContext.Current.CancellationToken));
        await _services.Received(1).UninstallServiceAsync(1, "nginx", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestServiceLogs_Authorized_ReturnsOkWithTaskId()
    {
        Allow(true);
        _services.CreateServiceLogsTaskAsync(1, "nginx", 100, false, Arg.Any<CancellationToken>()).Returns(42);

        Assert.IsType<OkObjectResult>((await _sut.RequestServiceLogs(1, new ServiceLogsRequest { ServiceName = "nginx", Lines = 100, Follow = false }, TestContext.Current.CancellationToken)).Result);
    }
}
