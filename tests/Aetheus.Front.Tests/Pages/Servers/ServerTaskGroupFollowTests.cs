// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R3-37, last two gaps: a Docker project's bulk action (one task per container) is followed
/// as one group until its last task ends, and the Certbot creation dialog tells the page's follower
/// about the task it queues, like every other server action.
/// </summary>
public sealed class ServerTaskGroupFollowTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerTaskGroupFollowTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private Task QueueAsync<T>(IRenderedComponent<T> cut, int id, string name) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.InvokeAsync(() => Tracker.Upsert(new ServerTaskDto { Id = id, ServerId = 10, Name = name, Status = TaskExecutionStatus.Pending }));

    private Task EndAsync<T>(IRenderedComponent<T> cut, int id, string name, TaskExecutionStatus status) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.InvokeAsync(() => Tracker.HandleTerminal(new TaskCompletedNotification { TaskId = id, ServerId = 10, TaskName = name, Status = status }));

    private IRenderedComponent<ServerTaskFollower> RenderFollower(string prefix = "Docker") =>
        Render<ServerTaskFollower>(p => p.Add(x => x.ServerId, 10).Add(x => x.NamePrefix, prefix));

    [Fact]
    public async Task AGroup_StaysOpenUntilItsLastTaskEnds_ThenIsDone()
    {
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.ExpectGroup("Restart web", ["Docker Restart - aaa111", "Docker Restart - bbb222"]));
        Assert.Contains("ServerTaskGroupProgress", cut.Find(".server-task-group-progress").TextContent);

        await QueueAsync(cut, 70001, "Docker Restart - aaa111");
        await QueueAsync(cut, 70002, "Docker Restart - bbb222");
        cut.WaitForAssertion(() => Assert.Equal(ServerTaskFollowState.Queued, cut.Instance.State));

        await EndAsync(cut, 70001, "Docker Restart - aaa111", TaskExecutionStatus.Success);
        cut.WaitForAssertion(() => Assert.Equal(1, cut.Instance.GroupFinished));
        Assert.Equal(ServerTaskFollowState.Queued, cut.Instance.State);
        Assert.DoesNotContain("ServerTaskSucceeded", cut.Markup);

        await EndAsync(cut, 70002, "Docker Restart - bbb222", TaskExecutionStatus.Success);
        cut.WaitForAssertion(() => Assert.Equal(ServerTaskFollowState.Succeeded, cut.Instance.State));
        Assert.Contains("ServerTaskSucceeded", cut.Markup);
    }

    [Fact]
    public async Task AGroupWithAFailedTask_FailsWithThatTasksCauseAndLink()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/logs/task/70004", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 70004, Message = "Error response from daemon: No such container: bbb222" }
        });
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.ExpectGroup("Stop web", ["Docker Stop - aaa111", "Docker Stop - bbb222"]));

        await EndAsync(cut, 70003, "Docker Stop - aaa111", TaskExecutionStatus.Success);
        await EndAsync(cut, 70004, "Docker Stop - bbb222", TaskExecutionStatus.Failed);

        cut.WaitForAssertion(() => Assert.Equal(ServerTaskFollowState.Failed, cut.Instance.State));
        Assert.Equal(70004, cut.Instance.TaskId);
        Assert.Contains("/tasks/70004", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains("No such container: bbb222", cut.Find(".task-output-error").TextContent));
    }

    [Fact]
    public async Task ATaskOfAnotherContainerOrAnOlderTask_IsNotPartOfTheGroup()
    {
        var cut = RenderFollower();
        await QueueAsync(cut, 70005, "Docker Restart - aaa111");
        await cut.InvokeAsync(() => cut.Instance.ExpectGroup("Restart web", ["Docker Restart - aaa111"]));

        await QueueAsync(cut, 70006, "Docker Restart - zzz999");
        await EndAsync(cut, 70005, "Docker Restart - aaa111", TaskExecutionStatus.Success);

        Assert.Equal(ServerTaskFollowState.Requested, cut.Instance.State);
        Assert.Equal(0, cut.Instance.GroupFinished);
    }

    private static ServerDetailDto DockerServer() => new()
    {
        Id = 10,
        Name = "docker-server",
        Hostname = "10.0.0.1",
        Type = ServerType.Docker,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Docker = new DockerDataDto
        {
            Containers =
            [
                new DockerContainerDto { ContainerId = "aaa111", Name = "web", Image = "nginx", State = "running", Project = "myapp", Status = "Up" },
                new DockerContainerDto { ContainerId = "bbb222", Name = "api", Image = "dotnet", State = "running", Project = "myapp", Status = "Up" },
                new DockerContainerDto { ContainerId = "ccc333", Name = "other", Image = "redis", State = "running", Project = "other", Status = "Up" }
            ],
            Images = [],
            ComposeStacks = [],
            Networks = [],
            Volumes = []
        }
    };

    private IRenderedComponent<DockerContainersTab> RenderContainers() =>
        Render<DockerContainersTab>(p => p.Add(x => x.Server, DockerServer()).Add(x => x.ServerId, 10).Add(x => x.InitialLoaded, true));

    // The bar's project picker, then its one visible action (Restart) or a menu item.
    private static async Task BulkAsync(IRenderedComponent<DockerContainersTab> cut, string project, string actionTitle)
    {
        var picker = cut.FindComponents<OmniDropDown<string>>().Single(drop => drop.Instance.Id == "docker-bulk-project");
        await cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(project));
        await cut.InvokeAsync(() => cut.Find($"[title='{actionTitle}']").Click());
    }

    [Fact]
    public async Task DockerProjectBulkAction_IsFollowedAsOneGroup_OfItsContainersTasks()
    {
        _handler.SetJsonResponse("api/servers/10/docker/action", true);
        var cut = RenderContainers();

        await BulkAsync(cut, "myapp", "RestartAll");

        var follower = cut.FindComponent<ServerTaskFollower>();
        Assert.Equal(2, follower.Instance.GroupCount);
        Assert.Equal(ServerTaskFollowState.Requested, follower.Instance.State);
        await QueueAsync(cut, 70010, "Docker Restart - ccc333");
        await QueueAsync(cut, 70011, "Docker Restart - aaa111");
        await QueueAsync(cut, 70012, "Docker Restart - bbb222");
        await EndAsync(cut, 70011, "Docker Restart - aaa111", TaskExecutionStatus.Success);
        await EndAsync(cut, 70010, "Docker Restart - ccc333", TaskExecutionStatus.Failed);
        await EndAsync(cut, 70012, "Docker Restart - bbb222", TaskExecutionStatus.Success);

        // The other project's failed task is not one of this click's.
        cut.WaitForAssertion(() => Assert.Equal(ServerTaskFollowState.Succeeded, follower.Instance.State));
    }

    [Fact]
    public async Task DockerProjectBulkAction_AllLaunchesRefused_LeavesNothingToFollow()
    {
        _handler.SetResponse("api/servers/10/docker/action", System.Net.HttpStatusCode.InternalServerError);
        var cut = RenderContainers();

        await BulkAsync(cut, "myapp", "RestartAll");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".server-task-follower")));
        Assert.Equal(ServerTaskFollowState.Idle, cut.FindComponent<ServerTaskFollower>().Instance.State);
    }

    private async Task CreateCertificateAsync(ServerTaskFollower follower)
    {
        var dialog = Render<ServerCertbotCreateDialog>(p => p.Add(x => x.ServerId, 10).Add(x => x.Follower, follower));
        await dialog.InvokeAsync(() => dialog.Find("#oe-pages-servers-serverdetailsections-servercertbotcreatedialog-1").Input("a.example, b.example"));
        dialog.FindAll("button").First(button => button.TextContent.Contains("Create")).Click();
        dialog.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.Contains("api/servers/10/certbot/create")));
    }

    [Fact]
    public async Task CertbotCreateDialog_TellsTheFollower_WhichBindsToTheCreateTask()
    {
        _handler.SetJsonResponse("api/servers/10/certbot/create", "{}");
        var follower = RenderFollower("Certbot");

        await CreateCertificateAsync(follower.Instance);

        Assert.Equal(ServerTaskFollowState.Requested, follower.Instance.State);
        Assert.Contains("NewCertificate a.example, b.example", follower.Markup);
        await QueueAsync(follower, 70020, "Certbot RenewalCheck");
        Assert.Null(follower.Instance.TaskId);
        await QueueAsync(follower, 70021, "Certbot create - a.example,b.example");
        follower.WaitForAssertion(() => Assert.Equal(70021, follower.Instance.TaskId));
    }

    [Fact]
    public async Task CertbotCreateDialog_ARefusedLaunch_LeavesNothingToFollow()
    {
        _handler.SetResponse("api/servers/10/certbot/create", System.Net.HttpStatusCode.InternalServerError);
        var follower = RenderFollower("Certbot");

        await CreateCertificateAsync(follower.Instance);

        follower.WaitForAssertion(() => Assert.Equal(ServerTaskFollowState.Idle, follower.Instance.State));
    }
}
