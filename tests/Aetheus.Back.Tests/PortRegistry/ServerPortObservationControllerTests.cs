// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.PortRegistry;

/// <summary>Refuse scans on incapable agents and prevent agents from replacing another server
/// observations.</summary>
public class ServerPortObservationControllerTests
{
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly IPortRegistryService _registry = Substitute.For<IPortRegistryService>();
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly IServerLifecycleService _servers = Substitute.For<IServerLifecycleService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly ServerPortObservationController _sut;

    public ServerPortObservationControllerTests()
    {
        _sut = new ServerPortObservationController(
            _registry, _tasks, _servers, _authz,
            new FakeTimeProvider(new DateTimeOffset(Now)),
            NullLogger<ServerPortObservationController>.Instance);
        SetUser(new Claim(ClaimTypes.NameIdentifier, "1"));
    }

    private void SetUser(params Claim[] claims) =>
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            }
        };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void AllowRead() => _authz
        .HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 7, Permission.Read, Arg.Any<CancellationToken>())
        .Returns(true);

    private void ServerIs(bool? capable) => _servers
        .GetServerDetailAsync(7, Arg.Any<CancellationToken>())
        .Returns(new ServerDetailDto { Id = 7, Name = "vps2577917", PortObservationAvailable = capable });

    [Fact]
    public async Task Observe_CapableAgent_QueuesTheTypedOperation()
    {
        AllowRead();
        ServerIs(true);
        _tasks.CreateOperationAsync(Arg.Any<CreateOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ServerTaskDto { Id = 42 });

        var result = await _sut.Observe(7, Ct);

        Assert.IsType<OkObjectResult>(result.Result);
        await _tasks.Received(1).CreateOperationAsync(
            Arg.Is<CreateOperationRequest>(request =>
                request.ServerId == 7 && request.Operation == OperationKind.PortsObserve),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Observe_AgentWithoutTheCapability_RefusesAndNamesTheFlag()
    {
        AllowRead();
        ServerIs(false);

        var result = await _sut.Observe(7, Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var error = Assert.IsType<ApiError>(bad.Value);
        Assert.Contains(AgentCapabilities.PortObservation, error.Message, StringComparison.Ordinal);
        await _tasks.DidNotReceive().CreateOperationAsync(
            Arg.Any<CreateOperationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Observe_AgentNeverReported_RefusesRatherThanQueueingBlind()
    {
        AllowRead();
        ServerIs(null);

        var result = await _sut.Observe(7, Ct);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Observe_WithoutServerRead_IsForbidden()
    {
        ServerIs(true);

        var result = await _sut.Observe(7, Ct);

        Assert.IsType<ForbidResult>(result.Result);
        await _tasks.DidNotReceive().CreateOperationAsync(
            Arg.Any<CreateOperationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Observe_UnknownServer_IsNotFound()
    {
        AllowRead();
        _servers.GetServerDetailAsync(7, Arg.Any<CancellationToken>()).Returns((ServerDetailDto?)null);

        var result = await _sut.Observe(7, Ct);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ReportObserved_OwnServer_HandsTheScanToTheRegistry()
    {
        SetUser(new Claim("ServerId", "7"));

        var result = await _sut.ReportObserved(7, new ObservedPortsReportDto
        {
            Ports = [new ObservedPortDto { Port = 80, Protocol = "tcp", Holder = "nginx", Interface = "0.0.0.0" }],
            ObservedAt = Now.AddYears(-3)
        }, Ct);

        Assert.IsType<NoContentResult>(result);
        // The agent's own timestamp is ignored on purpose: a skewed host clock would otherwise decide
        // what the UI shows as "last scan".
        await _registry.Received(1).ReplaceObservedAsync(
            7, Arg.Any<IReadOnlyCollection<ObservedPortDto>>(), Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportObserved_ForeignServer_IsForbidden()
    {
        SetUser(new Claim("ServerId", "8"));

        var result = await _sut.ReportObserved(7, new ObservedPortsReportDto(), Ct);

        Assert.IsType<ForbidResult>(result);
        await _registry.DidNotReceive().ReplaceObservedAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<ObservedPortDto>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportObserved_WithoutAServerClaim_IsForbidden()
    {
        var result = await _sut.ReportObserved(7, new ObservedPortsReportDto(), Ct);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ReportObserved_OversizedBatch_IsRefused()
    {
        SetUser(new Claim("ServerId", "7"));
        var flood = Enumerable
            .Range(1, PortRegistryLimits.MaxObservedPorts + 1)
            .Select(port => new ObservedPortDto { Port = port, Protocol = "tcp", Holder = "x", Interface = "0.0.0.0" })
            .ToList();

        var result = await _sut.ReportObserved(7, new ObservedPortsReportDto { Ports = flood }, Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        await _registry.DidNotReceive().ReplaceObservedAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<ObservedPortDto>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
