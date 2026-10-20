// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class LogHubTests
{
    private readonly LogHub _sut;
    private readonly ITaskService _taskServiceMock = Substitute.For<ITaskService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly HubCallerContext _contextMock = Substitute.For<HubCallerContext>();
    private readonly IGroupManager _groupsMock = Substitute.For<IGroupManager>();

    public LogHubTests()
    {
        _contextMock.ConnectionId.Returns("conn-1");
        _taskServiceMock.GetTaskServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        _authzMock.HasPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(true);
        _sut = new LogHub(_taskServiceMock, _authzMock);
        // Use reflection to set Hub properties
        typeof(Hub).GetProperty("Context")!.SetValue(_sut, _contextMock);
        typeof(Hub).GetProperty("Groups")!.SetValue(_sut, _groupsMock);
    }

    [Fact]
    public async Task JoinTaskGroup_AddsToCorrectGroup()
    {
        await _sut.JoinTaskGroup(42);

        await _groupsMock.Received(1).AddToGroupAsync("conn-1", "task-42", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveTaskGroup_RemovesFromCorrectGroup()
    {
        await _sut.LeaveTaskGroup(42);

        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-1", "task-42", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinTaskGroup_PermissionDenied_ThrowsAndDoesNotJoin()
    {
        _authzMock.HasPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinTaskGroup(42));

        await _groupsMock.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinTaskGroup_TaskNotFound_ThrowsAndDoesNotJoin()
    {
        _taskServiceMock.GetTaskServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinTaskGroup(42));

        await _groupsMock.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

public class ServerHubTests
{
    private readonly ServerHub _sut;
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly HubCallerContext _contextMock = Substitute.For<HubCallerContext>();
    private readonly IGroupManager _groupsMock = Substitute.For<IGroupManager>();

    public ServerHubTests()
    {
        _contextMock.ConnectionId.Returns("conn-2");
        _authzMock.HasPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(true);
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns((List<int>?)null);
        _authzMock.GetUserOrganizationIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new List<int>());
        _sut = new ServerHub(_authzMock);
        typeof(Hub).GetProperty("Context")!.SetValue(_sut, _contextMock);
        typeof(Hub).GetProperty("Groups")!.SetValue(_sut, _groupsMock);
    }

    [Fact]
    public async Task JoinServerGroup_AddsToCorrectGroup()
    {
        await _sut.JoinServerGroup(5);

        await _groupsMock.Received(1).AddToGroupAsync("conn-2", "server-5", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveServerGroup_RemovesFromCorrectGroup()
    {
        await _sut.LeaveServerGroup(5);

        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-2", "server-5", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAllServers_AddsToAllServersGroup()
    {
        await _sut.JoinAllServers();

        await _groupsMock.Received(1).AddToGroupAsync("conn-2", "all-servers", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinServerGroup_PermissionDenied_ThrowsAndDoesNotJoin()
    {
        _authzMock.HasPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinServerGroup(5));

        await _groupsMock.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAllServers_NoAccessibleResourcesNoOrgs_ThrowsAndDoesNotJoin()
    {
        // F-05: a non-admin with neither accessible resources nor org membership is rejected.
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(new List<int>());

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinAllServers());

        await _groupsMock.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAllServers_NonAdmin_JoinsPerResourceAndPerOrgGroups()
    {
        // F-05 CREATE blind-spot fix: a non-admin joins its readable server-{id} groups AND the per-org
        // aggregate group(s), so a server enrolled later (empty server-{id}) still reaches it live.
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(new List<int> { 5 });
        _authzMock.GetUserOrganizationIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new List<int> { 3 });

        await _sut.JoinAllServers();

        await _groupsMock.Received(1).AddToGroupAsync("conn-2", "server-5", Arg.Any<CancellationToken>());
        await _groupsMock.Received(1).AddToGroupAsync("conn-2", "server-org-3", Arg.Any<CancellationToken>());
        await _groupsMock.DidNotReceive().AddToGroupAsync("conn-2", "all-servers", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAllServers_OrgMemberWithNoServersYet_JoinsOrgGroupWithoutThrowing()
    {
        // The exact "added a server, nothing happened" case for a non-admin: the org has no servers yet,
        // so the accessible set is empty - but org membership alone must still subscribe to the org group
        // so the FIRST enrolled server appears live.
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(new List<int>());
        _authzMock.GetUserOrganizationIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new List<int> { 3 });

        await _sut.JoinAllServers();

        await _groupsMock.Received(1).AddToGroupAsync("conn-2", "server-org-3", Arg.Any<CancellationToken>());
    }
}

public class PipelineHubTests
{
    private readonly PipelineHub _sut;
    private readonly IPipelineRunLookupReader _pipelineRepoMock = Substitute.For<IPipelineRunLookupReader>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly HubCallerContext _contextMock = Substitute.For<HubCallerContext>();
    private readonly IGroupManager _groupsMock = Substitute.For<IGroupManager>();

    public PipelineHubTests()
    {
        _contextMock.ConnectionId.Returns("conn-3");
        _pipelineRepoMock.GetPipelineIdForRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        _authzMock.HasPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(true);
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns((List<int>?)null);
        _sut = new PipelineHub(_pipelineRepoMock, _authzMock);
        typeof(Hub).GetProperty("Context")!.SetValue(_sut, _contextMock);
        typeof(Hub).GetProperty("Groups")!.SetValue(_sut, _groupsMock);
    }

    [Fact]
    public async Task JoinPipelineRunGroup_AddsToCorrectGroup()
    {
        await _sut.JoinPipelineRunGroup(10);

        await _groupsMock.Received(1).AddToGroupAsync("conn-3", "pipeline-run-10", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeavePipelineRunGroup_RemovesFromCorrectGroup()
    {
        await _sut.LeavePipelineRunGroup(10);

        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-3", "pipeline-run-10", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinPipelineRunGroup_PermissionDenied_ThrowsAndDoesNotJoin()
    {
        _authzMock.HasPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinPipelineRunGroup(10));

        await _groupsMock.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinPipelineRunGroup_RunNotFound_ThrowsAndDoesNotJoin()
    {
        _pipelineRepoMock.GetPipelineIdForRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinPipelineRunGroup(10));

        await _groupsMock.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void PipelineRunUpdates_TargetsListsPipelineAndOpenRun()
    {
        var groups = HubGroups.PipelineRunUpdates(10, 7);

        Assert.Equal(["pipeline-updates", "pipeline-7", "pipeline-run-10"], groups);
    }

    [Fact]
    public void PipelineRunUpdates_WithoutPipelineStillTargetsOpenRun()
    {
        var groups = HubGroups.PipelineRunUpdates(10, null);

        Assert.Equal(["pipeline-updates", "pipeline-run-10"], groups);
    }
}

public class AlertHubTests
{
    private readonly AlertHub _sut;
    private readonly HubCallerContext _contextMock = Substitute.For<HubCallerContext>();
    private readonly IGroupManager _groupsMock = Substitute.For<IGroupManager>();

    public AlertHubTests()
    {
        _contextMock.ConnectionId.Returns("conn-4");
        _sut = new AlertHub();
        typeof(Hub).GetProperty("Context")!.SetValue(_sut, _contextMock);
        typeof(Hub).GetProperty("Groups")!.SetValue(_sut, _groupsMock);
    }

    [Fact]
    public async Task JoinAlertGroup_AddsToAlertsGroup()
    {
        await _sut.JoinAlertGroup();
        await _groupsMock.Received(1).AddToGroupAsync("conn-4", "alerts", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveAlertGroup_RemovesFromAlertsGroup()
    {
        await _sut.LeaveAlertGroup();
        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-4", "alerts", Arg.Any<CancellationToken>());
    }
}

public class ReleaseHubTests
{
    private readonly ReleaseHub _sut;
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly HubCallerContext _contextMock = Substitute.For<HubCallerContext>();
    private readonly IGroupManager _groupsMock = Substitute.For<IGroupManager>();

    public ReleaseHubTests()
    {
        _contextMock.ConnectionId.Returns("conn-5");
        _authzMock.HasPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(true);
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns((List<int>?)null);
        _sut = new ReleaseHub(_authzMock);
        typeof(Hub).GetProperty("Context")!.SetValue(_sut, _contextMock);
        typeof(Hub).GetProperty("Groups")!.SetValue(_sut, _groupsMock);
    }

    [Fact]
    public async Task JoinProjectGroup_AddsToCorrectGroup()
    {
        await _sut.JoinProjectGroup(7);
        await _groupsMock.Received(1).AddToGroupAsync("conn-5", "project-releases-7", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveProjectGroup_RemovesFromCorrectGroup()
    {
        await _sut.LeaveProjectGroup(7);
        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-5", "project-releases-7", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAllReleases_AddsToAllReleasesGroup()
    {
        await _sut.JoinAllReleases();
        await _groupsMock.Received(1).AddToGroupAsync("conn-5", "all-releases", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinProjectGroup_PermissionDenied_ThrowsAndDoesNotJoin()
    {
        _authzMock.HasPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinProjectGroup(7));

        await _groupsMock.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAllReleases_NoAccessibleResources_ThrowsAndDoesNotJoin()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(new List<int>());

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinAllReleases());

        await _groupsMock.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

public class EntityHubTests
{
    private readonly EntityHub _sut;
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly HubCallerContext _contextMock = Substitute.For<HubCallerContext>();
    private readonly IGroupManager _groupsMock = Substitute.For<IGroupManager>();

    public EntityHubTests()
    {
        _contextMock.ConnectionId.Returns("conn-6");
        _authzMock.GetUserOrganizationIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new List<int>());
        _sut = new EntityHub(_authzMock);
        typeof(Hub).GetProperty("Context")!.SetValue(_sut, _contextMock);
        typeof(Hub).GetProperty("Groups")!.SetValue(_sut, _groupsMock);
    }

    [Fact]
    public async Task JoinEntityUpdates_Admin_JoinsAggregateGroupOnly()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), ResourceType.Project, Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns((List<int>?)null);

        await _sut.JoinEntityUpdates(ResourceType.Project);

        await _groupsMock.Received(1).AddToGroupAsync("conn-6", "entity-Project-all", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinEntityUpdates_ProjectNonAdmin_JoinsPerResourceAndPerOrgGroups()
    {
        // F-05 CREATE blind-spot fix for the org-scoped Project type: a project created later has an
        // empty entity-Project-{id} group, so the per-org group is what reaches a non-admin member live.
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), ResourceType.Project, Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(new List<int> { 5 });
        _authzMock.GetUserOrganizationIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new List<int> { 3 });

        await _sut.JoinEntityUpdates(ResourceType.Project);

        await _groupsMock.Received(1).AddToGroupAsync("conn-6", "entity-Project-5", Arg.Any<CancellationToken>());
        await _groupsMock.Received(1).AddToGroupAsync("conn-6", "entity-Project-org-3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinEntityUpdates_PipelineNonAdmin_JoinsPerResourceAndPerOrgGroups()
    {
        _authzMock.GetAccessibleResourceIdsAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(), ResourceType.Pipeline,
                Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 8 });
        _authzMock.GetUserOrganizationIdsAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 3 });

        await _sut.JoinEntityUpdates(ResourceType.Pipeline);

        await _groupsMock.Received(1).AddToGroupAsync("conn-6", "entity-Pipeline-8", Arg.Any<CancellationToken>());
        await _groupsMock.Received(1).AddToGroupAsync("conn-6", "entity-Pipeline-org-3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveEntityUpdates_Admin_LeavesTheAggregateGroupOnly()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), ResourceType.Project, Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns((List<int>?)null);

        await _sut.LeaveEntityUpdates(ResourceType.Project);

        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-6", "entity-Project-all", Arg.Any<CancellationToken>());
        await _groupsMock.Received(1).RemoveFromGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveEntityUpdates_ProjectNonAdmin_LeavesPerResourceAndPerOrgGroups()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), ResourceType.Project, Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(new List<int> { 5, 6 });
        _authzMock.GetUserOrganizationIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new List<int> { 3 });

        await _sut.LeaveEntityUpdates(ResourceType.Project);

        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-6", "entity-Project-5", Arg.Any<CancellationToken>());
        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-6", "entity-Project-6", Arg.Any<CancellationToken>());
        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-6", "entity-Project-org-3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveEntityUpdates_NonOrgScopedType_DoesNotConsultOrganizations()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), ResourceType.Vault, Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(new List<int> { 9 });

        await _sut.LeaveEntityUpdates(ResourceType.Vault);

        await _groupsMock.Received(1).RemoveFromGroupAsync("conn-6", "entity-Vault-9", Arg.Any<CancellationToken>());
        await _groupsMock.Received(1).RemoveFromGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _authzMock.DidNotReceive().GetUserOrganizationIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinEntityUpdates_NonOrgScopedType_DoesNotJoinOrgGroup()
    {
        // Vault is not org-scoped: no org fan-out, and the org-id lookup must not even be consulted.
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), ResourceType.Vault, Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(new List<int> { 9 });

        await _sut.JoinEntityUpdates(ResourceType.Vault);

        await _groupsMock.Received(1).AddToGroupAsync("conn-6", "entity-Vault-9", Arg.Any<CancellationToken>());
        await _groupsMock.DidNotReceive().AddToGroupAsync("conn-6", Arg.Is<string>(g => g.Contains("-org-")), Arg.Any<CancellationToken>());
        await _authzMock.DidNotReceive().GetUserOrganizationIdsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }
}
