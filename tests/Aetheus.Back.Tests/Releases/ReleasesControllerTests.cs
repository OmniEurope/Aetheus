// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ReleasesControllerTests
{
    private readonly IReleaseService _serviceMock = Substitute.For<IReleaseService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly IConfiguration _configMock = Substitute.For<IConfiguration>();
    private readonly IPipelineRunService _pipelineRunService = Substitute.For<IPipelineRunService>();
    private readonly IReleaseDeploymentAnnouncer _announcer = Substitute.For<IReleaseDeploymentAnnouncer>();
    private readonly IReleaseStatusRestoreService _statusRestore = Substitute.For<IReleaseStatusRestoreService>();
    private readonly ReleasesController _sut;

    public ReleasesControllerTests()
    {
        _sut = new ReleasesController(_serviceMock, _authzMock, _configMock, _pipelineRunService, _announcer, _statusRestore);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim("ServerId", "7")], "test"))
            }
        };
    }

    [Fact]
    public async Task CreateRelease_AssignedAgentAndMatchingProject_CreatesRelease()
    {
        _pipelineRunService.IsServerAssignedToRunAsync(42, 7, Arg.Any<CancellationToken>()).Returns(true);
        _pipelineRunService.GetRunPipelineContextAsync(42, Arg.Any<CancellationToken>()).Returns((5, 3));
        _serviceMock.CreateReleaseFromPipelineAsync(
                3, 42, "1.2.3", null, null, null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 9, ProjectId = 3, Version = "1.2.3", Status = ReleaseStatus.Deployed });

        var result = await _sut.CreateRelease(
            new CreateReleaseRequest { PipelineRunId = 42, Version = "1.2.3", Deployed = true }, 3,
            TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        // The release step's deployment is announced, with the run and the agent that recorded it.
        await _announcer.Received(1).AnnounceAsync(9, 42, 7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRelease_NotMarkedDeployed_AnnouncesNoDeployment()
    {
        _pipelineRunService.IsServerAssignedToRunAsync(42, 7, Arg.Any<CancellationToken>()).Returns(true);
        _pipelineRunService.GetRunPipelineContextAsync(42, Arg.Any<CancellationToken>()).Returns((5, 3));
        _serviceMock.CreateReleaseFromPipelineAsync(
                3, 42, "1.2.3", null, null, null, null, null, false, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 9, ProjectId = 3, Version = "1.2.3", Status = ReleaseStatus.Published });

        await _sut.CreateRelease(
            new CreateReleaseRequest { PipelineRunId = 42, Version = "1.2.3" }, 3,
            TestContext.Current.CancellationToken);

        await _announcer.DidNotReceiveWithAnyArgs().AnnounceAsync(default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateRelease_AgentNotAssignedToRun_ReturnsForbid()
    {
        _pipelineRunService.IsServerAssignedToRunAsync(42, 7, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CreateRelease(
            new CreateReleaseRequest { PipelineRunId = 42, Version = "1.2.3" }, 3,
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceiveWithAnyArgs().CreateReleaseFromPipelineAsync(
            default, default, default!, default, default, default, default, default, default,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateRelease_RunBelongsToDifferentProject_ReturnsForbid()
    {
        _pipelineRunService.IsServerAssignedToRunAsync(42, 7, Arg.Any<CancellationToken>()).Returns(true);
        _pipelineRunService.GetRunPipelineContextAsync(42, Arg.Any<CancellationToken>()).Returns((5, 99));

        var result = await _sut.CreateRelease(
            new CreateReleaseRequest { PipelineRunId = 42, Version = "1.2.3" }, 3,
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceiveWithAnyArgs().CreateReleaseFromPipelineAsync(
            default, default, default!, default, default, default, default, default, default,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateRelease_WithoutRunId_ReturnsBadRequest()
    {
        var result = await _sut.CreateRelease(
            new CreateReleaseRequest { Version = "1.2.3" }, 3, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await _serviceMock.DidNotReceiveWithAnyArgs().CreateReleaseFromPipelineAsync(
            default, default, default!, default, default, default, default, default, default,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetReleases_ReturnsOk()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1 });
        _serviceMock.GetReleasesAsync(null, Arg.Any<PaginationRequest>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<ReleaseDto> { Items = [new ReleaseDto { Id = 1, Version = "1.0.0" }], TotalCount = 1 });

        var result = await _sut.GetReleases(null, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetReleases_NoAccessible_ReturnsEmptyOk()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int>());

        var result = await _sut.GetReleases(null, new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PaginatedResult<ReleaseDto>>(ok.Value);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task GetRelease_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetReleaseAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 1, Version = "1.0.0" });

        var result = await _sut.GetRelease(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetRelease_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetRelease(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetRelease_NotFound_ReturnsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetReleaseAsync(1, Arg.Any<CancellationToken>())
            .Returns((ReleaseDto?)null);

        var result = await _sut.GetRelease(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task SyncReleases_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.SyncReleasesAsync(1, Arg.Any<CancellationToken>())
            .Returns([new ReleaseDto { Id = 1, Version = "1.0.0" }]);

        var result = await _sut.SyncReleases(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task SyncReleases_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.SyncReleases(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    private PipelineRunPreparation AllowLaunch(int pipelineId, params int[] targets)
    {
        var preparation = new PipelineRunPreparation { PipelineId = pipelineId, YamlSnapshot = "name: p", TargetServerIds = targets };
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, pipelineId, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _pipelineRunService.PrepareRunAsync(pipelineId, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(preparation);
        _pipelineRunService.ResolveRunParametersAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<PipelineRunPreparation>(0));
        foreach (var target in targets)
            _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, target, Permission.Admin, Arg.Any<CancellationToken>())
                .Returns(true);
        return preparation;
    }

    [Fact]
    public async Task TriggerBuild_Authorized_ReturnsAccepted()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        var preparation = AllowLaunch(10, 42);
        _serviceMock.TriggerReleaseBuildAsync(1, Arg.Any<TriggerReleaseBuildRequest>(), Arg.Any<PipelineRunPreparation>(), Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 1, Version = "1.0.0" });

        var result = await _sut.TriggerBuild(1, new TriggerReleaseBuildRequest { PipelineId = 10 }, TestContext.Current.CancellationToken);

        Assert.IsType<AcceptedAtActionResult>(result.Result);
        await _serviceMock.Received(1).TriggerReleaseBuildAsync(1, Arg.Any<TriggerReleaseBuildRequest>(),
            Arg.Is<PipelineRunPreparation>(p => ReferenceEquals(p, preparation)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerBuild_WithoutServerAdminOnAPipelineTarget_ReturnsForbidBeforeLaunch()
    {
        // BACK-023: Release.Write alone launched a pipeline the direct route refuses to this caller.
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        AllowLaunch(10, 42);
        _pipelineRunService.PrepareRunAsync(10, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunPreparation { PipelineId = 10, YamlSnapshot = "name: p", TargetServerIds = [42, 43] });

        var result = await _sut.TriggerBuild(1, new TriggerReleaseBuildRequest { PipelineId = 10 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().TriggerReleaseBuildAsync(
            Arg.Any<int>(), Arg.Any<TriggerReleaseBuildRequest>(), Arg.Any<PipelineRunPreparation>(), Arg.Any<CancellationToken>());
    }

    private const string ReleaseCommit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task RelaunchFromCommit_ArchivedRelease_PreparesTheRunAtItsCommit_AndLaunchesIt()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 10, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetReleaseAsync(1, Arg.Any<CancellationToken>()).Returns(new ReleaseDto
        {
            Id = 1,
            Status = ReleaseStatus.Archived,
            CommitHash = ReleaseCommit,
            BranchName = "main"
        });
        var pinned = new PipelineRunPreparation { PipelineId = 10, YamlSnapshot = "name: p", TargetServerIds = [], CommitHash = ReleaseCommit };
        _pipelineRunService.PrepareRunAtCommitAsync(10, "main", ReleaseCommit, Arg.Any<CancellationToken>()).Returns(pinned);
        _pipelineRunService.ResolveRunParametersAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<PipelineRunPreparation>(0));
        _serviceMock.RelaunchFromCommitAsync(1, Arg.Any<TriggerReleaseBuildRequest>(), Arg.Any<PipelineRunPreparation>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunDto { Id = 77 });

        var result = await _sut.RelaunchFromCommit(1, new TriggerReleaseBuildRequest { PipelineId = 10 }, TestContext.Current.CancellationToken);

        Assert.Equal(77, Assert.IsType<PipelineRunDto>(Assert.IsType<AcceptedResult>(result.Result).Value).Id);
        await _pipelineRunService.DidNotReceive().PrepareRunAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).RelaunchFromCommitAsync(1, Arg.Any<TriggerReleaseBuildRequest>(),
            Arg.Is<PipelineRunPreparation>(p => ReferenceEquals(p, pinned)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ReleaseStatus.Published, ReleaseCommit)]
    [InlineData(ReleaseStatus.Archived, null)]
    [InlineData(ReleaseStatus.Archived, "abc1234")]
    public async Task RelaunchFromCommit_NotArchivedOrWithoutAFullCommit_IsRefusedBeforeAnyLaunch(ReleaseStatus status, string? commit)
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetReleaseAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 1, Status = status, CommitHash = commit });

        var result = await _sut.RelaunchFromCommit(1, new TriggerReleaseBuildRequest { PipelineId = 10 }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await _pipelineRunService.DidNotReceive().PrepareRunAtCommitAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().RelaunchFromCommitAsync(
            Arg.Any<int>(), Arg.Any<TriggerReleaseBuildRequest>(), Arg.Any<PipelineRunPreparation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RelaunchFromCommit_WithoutReleaseWrite_ReturnsForbid()
    {
        var result = await _sut.RelaunchFromCommit(1, new TriggerReleaseBuildRequest { PipelineId = 10 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().GetReleaseAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerBuild_WithoutPipelineWrite_ReturnsForbidBeforeLaunch()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.TriggerBuild(1, new TriggerReleaseBuildRequest { PipelineId = 10 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _pipelineRunService.DidNotReceive().PrepareRunAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().TriggerReleaseBuildAsync(
            Arg.Any<int>(), Arg.Any<TriggerReleaseBuildRequest>(), Arg.Any<PipelineRunPreparation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rollback_WithoutServerAdminOnAPipelineTarget_ReturnsForbidBeforeLaunch()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetReleaseAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 1, ProjectId = 7, Version = "1.0.0" });
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        AllowLaunch(5);
        _pipelineRunService.PrepareRunAsync(5, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunPreparation { PipelineId = 5, YamlSnapshot = "name: p", TargetServerIds = [42] });

        var result = await _sut.Rollback(1, new RollbackReleaseRequest { PipelineId = 5 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().RollbackReleaseAsync(
            Arg.Any<int>(), Arg.Any<RollbackReleaseRequest>(), Arg.Any<PipelineRunPreparation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rollback_Authorized_ReturnsAccepted()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetReleaseAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 1, ProjectId = 7, Version = "1.0.0" });
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        AllowLaunch(5, 42);
        _serviceMock.RollbackReleaseAsync(1, Arg.Any<RollbackReleaseRequest>(), Arg.Any<PipelineRunPreparation>(), Arg.Any<CancellationToken>())
            .Returns(new ReleaseRollbackDto { Id = 1, SourceReleaseId = 1, TargetReleaseId = 0 });

        var result = await _sut.Rollback(1, new RollbackReleaseRequest { PipelineId = 5 }, TestContext.Current.CancellationToken);

        Assert.IsType<AcceptedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Rollback_WithoutProjectWrite_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetReleaseAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 1, ProjectId = 7, Version = "1.0.0" });
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.Rollback(1, new RollbackReleaseRequest { PipelineId = 5 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().RollbackReleaseAsync(Arg.Any<int>(), Arg.Any<RollbackReleaseRequest>(), Arg.Any<PipelineRunPreparation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Promote_Authorized_ReturnsAccepted()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.PromoteReleaseAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 1, Version = "1.0.0" });

        var result = await _sut.Promote(1, TestContext.Current.CancellationToken);

        Assert.IsType<AcceptedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Webhook_NoSecret_ReturnsNotFoundAndProcessesNothing()
    {
        _configMock["Webhook:Secret"].Returns((string?)null);

        var result = await _sut.Webhook(TestContext.Current.CancellationToken);

        // A webhook that is not configured answers 404 with the ApiError body, never 503.
        var status = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, status.StatusCode);
        Assert.IsType<Aetheus.Shared.Components.Shared.ApiError>(status.Value);
        _serviceMock.DidNotReceiveWithAnyArgs().ValidateWebhookSignature(default, default!, default!);
        await _serviceMock.DidNotReceiveWithAnyArgs().HandleWebhookAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Webhook_InvalidSignature_ReturnsUnauthorized()
    {
        _configMock["Webhook:Secret"].Returns("secret123");
        _serviceMock.ValidateWebhookSignature(Arg.Any<string?>(), "secret123", Arg.Any<string>())
            .Returns(false);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"ref\":\"refs/tags/v1.0.0\"}"));
        httpContext.Request.Headers["X-Hub-Signature-256"] = "sha256=invalid";
        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await _sut.Webhook(TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Webhook_ValidPayload_ReturnsOk()
    {
        _configMock["Webhook:Secret"].Returns("secret123");
        _serviceMock.ValidateWebhookSignature(Arg.Any<string?>(), "secret123", Arg.Any<string>())
            .Returns(true);

        var body = "{\"ref\":\"refs/tags/v1.0.0\",\"repositoryUrl\":\"https://github.com/test\"}";
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        httpContext.Request.Headers["X-Hub-Signature-256"] = "sha256=valid";
        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await _sut.Webhook(TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        await _serviceMock.Received(1).HandleWebhookAsync(Arg.Any<WebhookPayload>(), Arg.Any<CancellationToken>());
    }

    // --- GetServerReleases (relocated from ServersController, route unchanged) ---

    [Fact]
    public async Task GetServerReleases_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetServerReleasesAsync(1, Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<ReleaseDto>());

        var result = await _sut.GetServerReleases(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    /// <summary>
    /// The endpoint changed controller, not resource: it is still the SERVER's Read permission that
    /// gates it. Authorizing against the release scope instead would silently widen access.
    /// </summary>
    [Fact]
    public async Task GetServerReleases_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetServerReleases(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // ---------- R-13: restore a release status ----------

    private static readonly RestoreReleaseStatusRequest RestoreToDeployed =
        new() { Status = ReleaseStatus.Deployed, Reason = "production still serves it" };

    private void GrantRestore(bool releaseWrite, bool projectAdmin)
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 9, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(releaseWrite);
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 5, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(projectAdmin);
        _serviceMock.GetReleaseAsync(9, Arg.Any<CancellationToken>()).Returns(new ReleaseDto { Id = 9, ProjectId = 5, Status = ReleaseStatus.Failed });
    }

    [Fact]
    public async Task R_13_RestoreStatus_WithReleaseWriteAndProjectAdmin_RestoresTheStatus()
    {
        GrantRestore(releaseWrite: true, projectAdmin: true);
        _statusRestore.RestoreAsync(9, RestoreToDeployed, Arg.Any<CancellationToken>())
            .Returns(new ReleaseDto { Id = 9, ProjectId = 5, Status = ReleaseStatus.Deployed });

        var result = await _sut.RestoreStatus(9, RestoreToDeployed, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(ReleaseStatus.Deployed, Assert.IsType<ReleaseDto>(ok.Value).Status);
    }

    [Fact]
    public async Task R_13_RestoreStatus_WithoutProjectAdmin_IsForbiddenAndChangesNothing()
    {
        GrantRestore(releaseWrite: true, projectAdmin: false);

        var result = await _sut.RestoreStatus(9, RestoreToDeployed, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        await _statusRestore.DidNotReceiveWithAnyArgs().RestoreAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task R_13_RestoreStatus_WithoutReleaseWrite_IsForbiddenAndChangesNothing()
    {
        GrantRestore(releaseWrite: false, projectAdmin: true);

        var result = await _sut.RestoreStatus(9, RestoreToDeployed, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        await _statusRestore.DidNotReceiveWithAnyArgs().RestoreAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task R_13_RestoreStatus_OfAnUnknownRelease_IsNotFound()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Release, 9, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.RestoreStatus(9, RestoreToDeployed, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
