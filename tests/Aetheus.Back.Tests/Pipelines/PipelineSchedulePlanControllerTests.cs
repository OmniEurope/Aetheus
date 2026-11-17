// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The organization rule of the schedule preview, which the RBAC check of the pipeline alone does not
/// settle: the permission answers through doubles here, so only the controller's own decisions are tested.
/// The same rule on the real HTTP pipeline is in <c>PipelineSchedulePreviewHttpIntegrationTests</c>.
/// </summary>
public sealed class PipelineSchedulePlanControllerTests
{
    private static readonly DateTime From = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);

    private readonly IPipelineSchedulePlanService _service = Substitute.For<IPipelineSchedulePlanService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();

    private PipelineSchedulePlanController Controller(bool admin)
    {
        var claims = admin ? [new Claim(ClaimTypes.Role, RoleNames.Admin)] : Array.Empty<Claim>();
        return new PipelineSchedulePlanController(_service, _authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
            }
        };
    }

    private void CanRead(bool granted) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 7, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(granted);

    private void PipelineOf(int? organizationId) =>
        _service.FindTargetAsync(7, Arg.Any<CancellationToken>())
            .Returns(new PipelineScheduleTarget(7, "nightly", "name: nightly\n", organizationId));

    private Task<ActionResult<SchedulePreviewDto>> Preview(PipelineSchedulePlanController controller) =>
        controller.GetSchedulePreview(7, ScheduleFrequency.EveryEvening, null, 1, 30, From, From.AddDays(1), TestContext.Current.CancellationToken);

    private Task<ActionResult<SchedulePlanDto>> Plan(PipelineSchedulePlanController controller, int organizationId, string[]? sourceBranch = null) =>
        controller.GetSchedulePlan(organizationId, From, From.AddDays(1), sourceBranch, TestContext.Current.CancellationToken);

    // R-5: the branch a reader picked per pipeline reaches the service as a map; a branch outside main/develop is a 400.
    [Fact]
    public async Task Plan_HandsTheServiceTheSourceBranchesTheReaderPicked()
    {
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([12]);
        var plan = new SchedulePlanDto { OrganizationId = 12 };
        _service.GetPlanAsync(
                12, From, From.AddDays(1), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<ServerActionScope>(),
                Arg.Is<IReadOnlyDictionary<int, string>?>(branches => branches != null && branches.Count == 1 && branches[7] == "develop"),
                Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = await Plan(Controller(admin: false), 12, ["7:develop"]);

        Assert.Same(plan, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Plan_WithABranchOutsideTheChoices_IsRefused_AndNothingIsRead()
    {
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([12]);

        await Assert.ThrowsAsync<BadRequestException>(() => Plan(Controller(admin: false), 12, ["7:feature/x"]));

        await _service.DidNotReceive().GetPlanAsync(
            Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>?>(),
            Arg.Any<ServerActionScope>(), Arg.Any<IReadOnlyDictionary<int, string>?>(), Arg.Any<CancellationToken>());
    }

    // R-8: the servers' scheduled actions follow the server rights, read to see and Administrator to move.
    [Fact]
    public async Task Plan_HandsTheServiceTheServersTheCallerReadsAndAdministers()
    {
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([12]);
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([7]);
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([3, 4]);
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns([4]);
        var plan = new SchedulePlanDto { OrganizationId = 12 };
        _service.GetPlanAsync(
                12, From, From.AddDays(1), Arg.Any<IReadOnlyCollection<int>?>(),
                Arg.Is<ServerActionScope>(scope => scope.ReadableServerIds!.SequenceEqual(new[] { 3, 4 }) && scope.AdministeredServerIds!.SequenceEqual(new[] { 4 })),
                Arg.Any<IReadOnlyDictionary<int, string>?>(), Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = await Plan(Controller(admin: false), 12);

        Assert.Same(plan, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    // R-8: an AI task follows the rights of its owner (project or server), a backup those of its project: read to see,
    // write to move, each list read separately.
    [Fact]
    public async Task Plan_HandsTheServiceTheProjectsAndServersTheCallerMayWrite_ForAiTasksAndBackups()
    {
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([12]);
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([10, 11]);
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Write, Arg.Any<CancellationToken>())
            .Returns([11]);
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns([4]);
        var plan = new SchedulePlanDto { OrganizationId = 12 };
        _service.GetPlanAsync(
                12, From, From.AddDays(1), Arg.Any<IReadOnlyCollection<int>?>(),
                Arg.Is<ServerActionScope>(scope => scope.ReadableProjectIds!.SequenceEqual(new[] { 10, 11 })
                    && scope.WritableProjectIds!.SequenceEqual(new[] { 11 })
                    && scope.WritableServerIds!.SequenceEqual(new[] { 4 })),
                Arg.Any<IReadOnlyDictionary<int, string>?>(), Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = await Plan(Controller(admin: false), 12);

        Assert.Same(plan, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    // R-8: the CVE watch is global data whose sources page is for site administrators: only they get it in the scope.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Plan_LetsOnlyASiteAdminSeeTheCveWatch(bool admin)
    {
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([12]);
        var plan = new SchedulePlanDto { OrganizationId = 12 };
        _service.GetPlanAsync(
                12, From, From.AddDays(1), Arg.Any<IReadOnlyCollection<int>?>(),
                Arg.Is<ServerActionScope>(scope => scope.SeesCveWatch == admin),
                Arg.Any<IReadOnlyDictionary<int, string>?>(), Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = await Plan(Controller(admin), 12);

        Assert.Same(plan, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Plan_ForAnotherOrganization_IsForbidden_AndNoServerActionIsRead()
    {
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([3]);

        var result = await Plan(Controller(admin: false), 12);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().GetPlanAsync(
            Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>?>(),
            Arg.Any<ServerActionScope>(), Arg.Any<IReadOnlyDictionary<int, string>?>(), Arg.Any<CancellationToken>());
        await _authz.DidNotReceive().GetAccessibleResourceIdsAsync(
            Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Arg.Any<Permission>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemberOfAnotherOrganization_IsForbidden_EvenWithTheReadPermission()
    {
        CanRead(true);
        PipelineOf(12);
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([3, 4]);

        var result = await Preview(Controller(admin: false));

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().PreviewAsync(
            Arg.Any<PipelineScheduleTarget>(), Arg.Any<ScheduleFrequencySpec>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PipelineOfNoOrganization_IsForbidden_ToANonAdmin()
    {
        CanRead(true);
        PipelineOf(null);
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([3]);

        Assert.IsType<ForbidResult>((await Preview(Controller(admin: false))).Result);
    }

    [Fact]
    public async Task WithoutTheReadPermission_IsForbidden_WithoutLookingTheTargetUp()
    {
        CanRead(false);

        Assert.IsType<ForbidResult>((await Preview(Controller(admin: false))).Result);
        await _service.DidNotReceive().FindTargetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownPipeline_IsNotFound()
    {
        CanRead(true);
        _service.FindTargetAsync(7, Arg.Any<CancellationToken>()).Returns((PipelineScheduleTarget?)null);

        Assert.IsType<NotFoundResult>((await Preview(Controller(admin: true))).Result);
    }

    [Fact]
    public async Task MemberOfTheOrganization_GetsThePreview_LimitedToItsReadablePipelines()
    {
        CanRead(true);
        PipelineOf(12);
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([12]);
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([7, 8]);
        var preview = new SchedulePreviewDto { PipelineId = 7 };
        _service.PreviewAsync(
                Arg.Any<PipelineScheduleTarget>(), Arg.Any<ScheduleFrequencySpec>(), From, From.AddDays(1),
                Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 7, 8 })), Arg.Any<CancellationToken>())
            .Returns(preview);

        var result = await Preview(Controller(admin: false));

        Assert.Same(preview, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SiteAdmin_ReadsAPipelineOfNoOrganization_WithoutAnOrganizationLookup()
    {
        CanRead(true);
        PipelineOf(null);
        _service.PreviewAsync(
                Arg.Any<PipelineScheduleTarget>(), Arg.Any<ScheduleFrequencySpec>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new SchedulePreviewDto { PipelineId = 7 });

        var result = await Preview(Controller(admin: true));

        Assert.IsType<OkObjectResult>(result.Result);
        await _authz.DidNotReceive().GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }
}
