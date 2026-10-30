// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Projects;

/// <summary> Recette R-97 (ADR-058): every endpoint of a project deletion's server side needs Admin on the
/// project, the permission the deletion itself needs. The authorization service grants it only within the
/// project's organization, so a member of another organization is refused before any agent is asked. </summary>
public sealed class ProjectDeletionControllerTests
{
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IProjectServerCleanupService _cleanup = Substitute.For<IProjectServerCleanupService>();
    private readonly IProjectDeletionService _deletion = Substitute.For<IProjectDeletionService>();

    private static readonly ClaimsPrincipal Caller = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "5")], "test"));

    private void Grant(bool admin) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(admin);

    private ProjectDeletionController Controller() => new(_authz, _cleanup)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Caller } }
    };

    private ProjectsController Projects() => new(Substitute.For<IProjectService>(), _authz, _deletion)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Caller } }
    };

    [Fact]
    public async Task WithoutAdminOnTheProject_EveryEndpointIsForbidden_AndNothingIsQueuedNorRead()
    {
        Grant(false);
        var ct = TestContext.Current.CancellationToken;

        Assert.IsType<ForbidResult>((await Controller().Discover(7, ct)).Result);
        Assert.IsType<ForbidResult>((await Controller().GetTasks(7, [41], ct)).Result);
        Assert.IsType<ForbidResult>((await Controller().Cleanup(7, new ProjectServerCleanupRequest { DiscoveryTaskIds = [41], AcknowledgeVolumeDataLoss = true }, ct)).Result);
        Assert.IsType<ForbidResult>(await Projects().DeleteProject(7, [41, 42], ct));

        Assert.Empty(_cleanup.ReceivedCalls());
        Assert.Empty(_deletion.ReceivedCalls());
        await _authz.Received(4).HasPermissionAsync(Caller, ResourceType.Project, 7, Permission.Admin, ct);
    }

    [Fact]
    public async Task WithAdmin_TheCallsReachTheService_ForThisProjectOnly()
    {
        Grant(true);
        var ct = TestContext.Current.CancellationToken;
        var request = new ProjectServerCleanupRequest { DiscoveryTaskIds = [41], AcknowledgeVolumeDataLoss = true };
        _deletion.DeleteAsync(7, Arg.Any<IReadOnlyCollection<int>>(), ct).Returns(ProjectDeletionOutcome.Deleted);

        Assert.IsType<OkObjectResult>((await Controller().Discover(7, ct)).Result);
        Assert.IsType<OkObjectResult>((await Controller().GetTasks(7, [41], ct)).Result);
        Assert.IsType<OkObjectResult>((await Controller().Cleanup(7, request, ct)).Result);
        Assert.IsType<NoContentResult>(await Projects().DeleteProject(7, [41, 42], ct));

        await _cleanup.Received(1).QueueDiscoveryAsync(7, ct);
        await _cleanup.Received(1).GetTasksAsync(7, Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 41 })), ct);
        await _cleanup.Received(1).QueueRemovalAsync(7, request, ct);
        await _deletion.Received(1).DeleteAsync(7, Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 41, 42 })), ct);
    }

    [Fact]
    public async Task TooManyTaskIds_AreRefused_BeforeTheServiceIsCalled()
    {
        Grant(true);
        var ct = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(1, ProjectServerResourceLimits.MaxTaskIds + 1).ToList();

        Assert.IsType<BadRequestObjectResult>((await Controller().GetTasks(7, ids, ct)).Result);
        Assert.IsType<BadRequestObjectResult>(await Projects().DeleteProject(7, ids, ct));
        Assert.Empty(_cleanup.ReceivedCalls());
        Assert.Empty(_deletion.ReceivedCalls());
    }

    [Fact]
    public async Task R51_ADeletionLeftWaitingForItsServerCleanup_Answers202_AndAMissingProject404()
    {
        Grant(true);
        var ct = TestContext.Current.CancellationToken;
        _deletion.DeleteAsync(7, Arg.Any<IReadOnlyCollection<int>>(), ct).Returns(ProjectDeletionOutcome.PendingCleanup);

        var accepted = Assert.IsType<StatusCodeResult>(await Projects().DeleteProject(7, [41], ct));
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);

        _deletion.DeleteAsync(7, Arg.Any<IReadOnlyCollection<int>>(), ct).Returns((ProjectDeletionOutcome?)null);
        Assert.IsType<NotFoundResult>(await Projects().DeleteProject(7, [41], ct));
    }
}
