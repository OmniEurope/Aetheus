// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Tasks;

/// <summary>A typed operation is queued only with a kind, a target valid for that kind, write access to
/// the server and what the kind's dedicated endpoint requires (BACK-003); an agent reports a refused deployment build only for a task of its own server.</summary>
public sealed class TasksControllerOperationTests
{
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();

    private TasksController Build(params Claim[] claims) => new(_tasks, _authz)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        },
    };

    private static CreateOperationRequest Operation(OperationKind kind, string target) =>
        new() { ServerId = 4, Name = "op", Operation = kind, Target = target };

    private void CanWrite(bool allowed) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 4, Permission.Write, Arg.Any<CancellationToken>()).Returns(allowed);

    [Fact]
    public async Task AnOperationWithoutKind_IsABadRequest()
    {
        var result = await Build().CreateOperation(Operation(OperationKind.None, "web"), TestContext.Current.CancellationToken);

        Assert.Contains("InvalidOperation", Assert.IsType<BadRequestObjectResult>(result.Result).Value!.ToString(), StringComparison.Ordinal);
        await _tasks.DidNotReceiveWithAnyArgs().CreateOperationAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ATargetInvalidForTheKind_IsABadRequestBeforeAuthorization()
    {
        var result = await Build().CreateOperation(Operation(OperationKind.DockerRestartContainer, "web; rm -rf /"), TestContext.Current.CancellationToken);

        Assert.Contains("InvalidTarget", Assert.IsType<BadRequestObjectResult>(result.Result).Value!.ToString(), StringComparison.Ordinal);
        await _authz.DidNotReceiveWithAnyArgs().HasPermissionAsync(default(ClaimsPrincipal)!, default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ACallerWithoutWriteAccess_IsForbidden()
    {
        CanWrite(false);

        var result = await Build().CreateOperation(Operation(OperationKind.DockerRestartContainer, "web"), TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _tasks.DidNotReceiveWithAnyArgs().CreateOperationAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AValidOperation_IsQueuedAndPointsAtItsTask()
    {
        CanWrite(true);
        var request = Operation(OperationKind.DockerRestartContainer, "web");
        _tasks.CreateOperationAsync(request, Arg.Any<CancellationToken>()).Returns(new ServerTaskDto { Id = 31 });

        var result = await Build().CreateOperation(request, TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(TasksController.GetTask), created.ActionName);
        Assert.Equal(31, Assert.IsType<ServerTaskDto>(created.Value).Id);
    }

    [Fact]
    public async Task AFirewallKind_WithWriteButWithoutTheFirewallKey_IsForbidden()
    {
        CanWrite(true);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.ServerFirewallManage, 4, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Build().CreateOperation(Operation(OperationKind.FirewallAllow, "8443"), TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _tasks.DidNotReceiveWithAnyArgs().CreateOperationAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFirewallKind_WithWriteAndTheFirewallKey_IsQueued()
    {
        CanWrite(true);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.ServerFirewallManage, 4, Arg.Any<CancellationToken>()).Returns(true);
        var request = Operation(OperationKind.FirewallAllow, "8443");
        _tasks.CreateOperationAsync(request, Arg.Any<CancellationToken>()).Returns(new ServerTaskDto { Id = 32 });

        Assert.IsType<CreatedAtActionResult>((await Build().CreateOperation(request, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task AnAdminKind_WithWriteOnly_IsForbidden()
    {
        CanWrite(true);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 4, Permission.Admin, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Build().CreateOperation(Operation(OperationKind.DockerExecScript, "abcdef123456"), TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _tasks.DidNotReceiveWithAnyArgs().CreateOperationAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void EveryOperationKind_HasARequirement_AndAnUnknownValueHasNone()
    {
        var unclassified = Enum.GetValues<OperationKind>()
            .Where(kind => kind != OperationKind.None && OperationKindPermissions.For(kind) is null)
            .ToList();

        Assert.Empty(unclassified);
        Assert.Null(OperationKindPermissions.For(OperationKind.None));
        Assert.Null(OperationKindPermissions.For((OperationKind)987_654));
    }

    [Theory]
    [InlineData(OperationKind.FirewallAllow, PermissionKeys.ServerFirewallManage)]
    [InlineData(OperationKind.FirewallSetEnabled, PermissionKeys.ServerFirewallManage)]
    [InlineData(OperationKind.SystemPackageUpgrade, PermissionKeys.ServerSystemUpgrade)]
    [InlineData(OperationKind.ServiceRestart, PermissionKeys.ServerServicesControl)]
    [InlineData(OperationKind.ServiceInstall, PermissionKeys.ServerServicesControl)]
    public void AKindWithANamedPermission_RequiresThatKey(OperationKind kind, string key) =>
        Assert.Equal(new OperationPermission(Permission.Write, key), OperationKindPermissions.For(kind));

    [Theory]
    [InlineData(OperationKind.DockerExecScript)]
    [InlineData(OperationKind.TeamspeakServerQuery)]
    [InlineData(OperationKind.ServerReboot)]
    [InlineData(OperationKind.AgentSelfUpdate)]
    [InlineData(OperationKind.PipelineDeploy)]
    public void AnAdminGatedKind_RequiresServerAdmin(OperationKind kind) =>
        Assert.Equal(new OperationPermission(Permission.Admin), OperationKindPermissions.For(kind));

    // BACK-004: a refusal closes the task, so it carries the fenced lease a completion carries.
    private static readonly DeploymentBuildRefusalReport Refusal = new()
    {
        TaskId = 11,
        Reason = "stale build",
        AgentSessionId = "0123456789abcdef0123456789abcdef",
        AgentSessionFencingToken = 3
    };

    [Fact]
    public async Task ARefusalWithoutLease_FromACurrentAgent_IsABadRequest_AndNeverReachesTheService()
    {
        _tasks.GetTaskServerIdAsync(11, Arg.Any<CancellationToken>()).Returns(4);
        _tasks.AllowsLegacyUnfencedTaskProtocolAsync(4, Arg.Any<CancellationToken>()).Returns(false);

        var unfenced = Refusal with { AgentSessionId = null, AgentSessionFencingToken = null };

        Assert.IsType<BadRequestResult>(await Build(new Claim("ServerId", "4")).ReportDeploymentBuildRefusal(11, unfenced, TestContext.Current.CancellationToken));
        await _tasks.DidNotReceiveWithAnyArgs().ReportDeploymentBuildRefusalAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARefusalForAnUnknownTask_IsNotFound()
    {
        _tasks.GetTaskServerIdAsync(11, Arg.Any<CancellationToken>()).Returns((int?)null);

        Assert.IsType<NotFoundResult>(await Build(new Claim("ServerId", "4")).ReportDeploymentBuildRefusal(11, Refusal, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARefusalFromAnotherServersAgent_IsForbidden()
    {
        _tasks.GetTaskServerIdAsync(11, Arg.Any<CancellationToken>()).Returns(5);

        Assert.IsType<ForbidResult>(await Build(new Claim("ServerId", "4")).ReportDeploymentBuildRefusal(11, Refusal, TestContext.Current.CancellationToken));
        await _tasks.DidNotReceiveWithAnyArgs().ReportDeploymentBuildRefusalAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true, typeof(OkResult))]
    [InlineData(false, typeof(ConflictResult))]
    public async Task ARefusalFromTheTasksOwnAgent_IsRecordedByTheService(bool recorded, Type expected)
    {
        _tasks.GetTaskServerIdAsync(11, Arg.Any<CancellationToken>()).Returns(4);
        _tasks.ReportDeploymentBuildRefusalAsync(11, Refusal, Arg.Any<CancellationToken>()).Returns(recorded);

        Assert.IsType(expected, await Build(new Claim("ServerId", "4")).ReportDeploymentBuildRefusal(11, Refusal, TestContext.Current.CancellationToken));
    }
}
