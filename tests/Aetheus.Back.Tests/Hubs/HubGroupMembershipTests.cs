// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Aetheus.Back.Tests.Hubs;

/// <summary>F-05: an administrator joins and leaves the aggregate group, every other user the
/// per-resource groups they can read, and a user who can read nothing is refused rather than
/// silently subscribed to nothing.</summary>
public class HubGroupMembershipTests
{
    private const string ConnectionId = "conn-1";

    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IGroupManager _groups = Substitute.For<IGroupManager>();

    private T Wire<T>(T hub) where T : Hub
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity("test")));
        hub.Context = context;
        hub.Groups = _groups;
        return hub;
    }

    private void Accessible(ResourceType type, List<int>? ids) =>
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), type, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(ids);

    private void Organizations(params int[] ids) =>
        _authz.GetOrganizationIdsWithPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns([.. ids]);

    private void AssertLeft(params string[] groups)
    {
        foreach (var group in groups)
            _groups.Received(1).RemoveFromGroupAsync(ConnectionId, group, Arg.Any<CancellationToken>());
        Assert.Equal(groups.Length, _groups.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IGroupManager.RemoveFromGroupAsync)));
    }

    private void AssertJoined(params string[] groups)
    {
        foreach (var group in groups)
            _groups.Received(1).AddToGroupAsync(ConnectionId, group, Arg.Any<CancellationToken>());
        Assert.Equal(groups.Length, _groups.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IGroupManager.AddToGroupAsync)));
    }

    [Fact]
    public async Task ServerHub_LeaveAllServers_AdministratorLeavesTheAggregateGroup()
    {
        Accessible(ResourceType.Server, null);

        await Wire(new ServerHub(_authz)).LeaveAllServers();

        AssertLeft(HubGroups.AllServers);
    }

    [Fact]
    public async Task ServerHub_LeaveAllServers_OtherUserLeavesEachServerAndOrganizationGroup()
    {
        Accessible(ResourceType.Server, [4, 9]);
        Organizations(7);

        await Wire(new ServerHub(_authz)).LeaveAllServers();

        AssertLeft(HubGroups.Server(4), HubGroups.Server(9), HubGroups.ServerOrg(7));
    }

    [Fact]
    public async Task ServerHub_JoinAllServers_UserWithNoReadableServerAndNoOrganizationIsRefused()
    {
        Accessible(ResourceType.Server, []);
        Organizations();

        await Assert.ThrowsAsync<HubException>(() => Wire(new ServerHub(_authz)).JoinAllServers());

        AssertJoined();
    }

    [Fact]
    public async Task ServerHub_JoinAllServers_OtherUserJoinsEachServerAndOrganizationGroup()
    {
        Accessible(ResourceType.Server, [4]);
        Organizations(7);

        await Wire(new ServerHub(_authz)).JoinAllServers();

        AssertJoined(HubGroups.Server(4), HubGroups.ServerOrg(7));
    }

    [Fact]
    public async Task PipelineHub_JoinPipelineUpdatesGroup_AdministratorJoinsTheAggregateGroup()
    {
        Accessible(ResourceType.Pipeline, null);

        await Wire(new PipelineHub(Substitute.For<IPipelineRunLookupReader>(), _authz)).JoinPipelineUpdatesGroup();

        AssertJoined(HubGroups.PipelineUpdates);
    }

    [Fact]
    public async Task PipelineHub_JoinPipelineUpdatesGroup_OtherUserJoinsOnlyTheirPipelines()
    {
        Accessible(ResourceType.Pipeline, [3, 8]);

        await Wire(new PipelineHub(Substitute.For<IPipelineRunLookupReader>(), _authz)).JoinPipelineUpdatesGroup();

        AssertJoined(HubGroups.Pipeline(3), HubGroups.Pipeline(8));
    }

    [Fact]
    public async Task PipelineHub_JoinPipelineUpdatesGroup_UserWhoReadsNoPipelineIsRefused()
    {
        Accessible(ResourceType.Pipeline, []);

        await Assert.ThrowsAsync<HubException>(() =>
            Wire(new PipelineHub(Substitute.For<IPipelineRunLookupReader>(), _authz)).JoinPipelineUpdatesGroup());

        AssertJoined();
    }

    [Fact]
    public async Task PipelineHub_LeavePipelineUpdatesGroup_MirrorsTheJoin()
    {
        Accessible(ResourceType.Pipeline, [3, 8]);
        await Wire(new PipelineHub(Substitute.For<IPipelineRunLookupReader>(), _authz)).LeavePipelineUpdatesGroup();
        AssertLeft(HubGroups.Pipeline(3), HubGroups.Pipeline(8));

        _groups.ClearReceivedCalls();
        Accessible(ResourceType.Pipeline, null);
        await Wire(new PipelineHub(Substitute.For<IPipelineRunLookupReader>(), _authz)).LeavePipelineUpdatesGroup();
        AssertLeft(HubGroups.PipelineUpdates);
    }
}
