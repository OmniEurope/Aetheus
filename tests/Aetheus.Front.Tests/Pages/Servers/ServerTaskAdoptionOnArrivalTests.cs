// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-12: leaving a server page and coming back while its action still runs shows the action again. Each
/// page below is rendered with the task already in the live feed (what the tracker holds after the user came back)
/// and must follow it, with the task named by the shared <see cref="ServerTaskNames"/> the backend also uses. A task
/// that ended while the user was away is not in the feed and is never shown as running.
/// </summary>
public sealed class ServerTaskAdoptionOnArrivalTests : BunitContext
{
    private const int ServerId = 21;

    private readonly BunitTestHelper.TestHandler _handler;

    public ServerTaskAdoptionOnArrivalTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, extraPermissionKeys: [PermissionKeys.ServerFirewallManage]);
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private static ServerTaskDto InFlight(int id, string name, TaskExecutionStatus status = TaskExecutionStatus.Running) =>
        new() { Id = id, ServerId = ServerId, Name = name, Status = status };

    private IRenderedComponent<ServerFirewallSection> RenderFirewall()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "firewall", new ServerFirewallDto
        {
            Installed = true,
            Active = true,
            StatusKnown = true,
            FirewallManagementAvailable = true,
            Rules = []
        });
        return Render<ServerFirewallSection>(p => p.Add(c => c.ServerId, ServerId).Add(c => c.FirewallManagementAvailable, true));
    }

    private IRenderedComponent<ServerPatchSection> RenderPatch()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "security-updates", new ServerSecurityUpdatesDto
        {
            Known = true,
            PackageManagerPresent = true,
            PatchManagementAvailable = true,
            CheckedAt = DateTime.Now
        });
        return Render<ServerPatchSection>(p => p.Add(c => c.ServerId, ServerId).Add(c => c.PatchManagementAvailable, true));
    }

    private IRenderedComponent<ServerPortsSection> RenderPorts()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "/projects", new List<ProjectDto>());
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/range", new PortRangeDto
        {
            From = PortRegistryLimits.DefaultRangeFrom,
            To = PortRegistryLimits.DefaultRangeTo,
            IsExplicit = false
        });
        _handler.SetJsonResponse(HttpMethod.Get, "/ports", new List<PortReservationDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "/ports/check", new PortCheckResultDto { ServerId = ServerId });
        return Render<ServerPortsSection>(p => p
            .Add(c => c.ServerId, ServerId)
            .Add(c => c.PortObservationAvailable, true)
            .Add(c => c.AgentOnline, true));
    }

    private IRenderedComponent<ServerServicesSection> RenderServices()
    {
        var server = new ServerDetailDto
        {
            Id = ServerId,
            Name = "adopt-srv",
            Hostname = "10.0.0.21",
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            Tags = [],
            Services = []
        };
        return Render<ServerServicesSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, ServerId));
    }

    private static void AssertFollows(IRenderedComponent<ServerTaskFollower> follower, int taskId, string name)
    {
        Assert.Equal(taskId, follower.Instance.TaskId);
        Assert.Contains($"/tasks/{taskId}", follower.Markup, StringComparison.Ordinal);
        // R-12: the follower shows the page's own name for the task (see ServerAdoptedTaskTitleTests), not the raw one.
        Assert.False(string.IsNullOrWhiteSpace(follower.Find(".server-task-follower strong").TextContent), name);
    }

    [Theory]
    [InlineData("Firewall enable")]
    [InlineData("Firewall allow 8080/tcp")]
    public void R12_TheFirewallPage_FollowsAFirewallTaskAlreadyRunning(string name)
    {
        Tracker.Upsert(InFlight(70001, name));

        var cut = RenderFirewall();

        AssertFollows(cut.FindComponent<ServerTaskFollower>(), 70001, name);
    }

    [Theory]
    [InlineData(ServerTaskNames.PatchCheck)]
    [InlineData(ServerTaskNames.PatchUpgrade)]
    public void R12_ThePatchPage_FollowsAnUpdateTaskAlreadyRunning(string name)
    {
        Tracker.Upsert(InFlight(70002, name));

        var cut = RenderPatch();

        AssertFollows(cut.FindComponent<ServerTaskFollower>(), 70002, name);
    }

    [Fact]
    public void R12_ThePortsPage_FollowsAScanAlreadyRunning()
    {
        Tracker.Upsert(InFlight(70003, ServerTaskNames.PortObservation, TaskExecutionStatus.Pending));

        var cut = RenderPorts();

        AssertFollows(cut.FindComponent<ServerTaskFollower>(), 70003, ServerTaskNames.PortObservation);
    }

    [Theory]
    [InlineData("Service Restart - nginx")]
    [InlineData("Install - nginx")]
    [InlineData("Uninstall - nginx")]
    public void R12_TheServicesPage_FollowsAServiceTaskAlreadyRunning(string name)
    {
        Tracker.Upsert(InFlight(70004, name));

        var cut = RenderServices();

        AssertFollows(cut.FindComponents<ServerTaskFollower>().Single(follower => follower.Instance.TaskId == 70004), 70004, name);
    }

    [Fact]
    public void R12_APageDoesNotFollow_ATaskOfAnotherPage()
    {
        Tracker.Upsert(InFlight(70005, "Apache restart"));
        Tracker.Upsert(InFlight(70006, ServerTaskNames.PortObservation));
        Tracker.Upsert(InFlight(70007, "Firewall enable"));

        var patch = RenderPatch();
        var services = RenderServices();

        Assert.Equal(ServerTaskFollowState.Idle, patch.FindComponent<ServerTaskFollower>().Instance.State);
        Assert.All(services.FindComponents<ServerTaskFollower>(), follower => Assert.Equal(ServerTaskFollowState.Idle, follower.Instance.State));
    }

    [Fact]
    public void R12_ATaskThatEndedWhileTheUserWasAway_IsNotShownAsRunning()
    {
        Tracker.Upsert(InFlight(70008, "Firewall enable"));
        Tracker.HandleTerminal(new TaskCompletedNotification
        {
            TaskId = 70008,
            ServerId = ServerId,
            TaskName = "Firewall enable",
            Status = TaskExecutionStatus.Success
        });
        // A late event that still lists the task with an end status must not bring it back either.
        Tracker.Upsert(InFlight(70009, "Firewall disable", TaskExecutionStatus.Success));

        var cut = RenderFirewall();

        var follower = cut.FindComponent<ServerTaskFollower>();
        Assert.Equal(ServerTaskFollowState.Idle, follower.Instance.State);
        Assert.Null(follower.Instance.TaskId);
        Assert.Empty(cut.FindAll(".server-task-follower"));
    }
}
