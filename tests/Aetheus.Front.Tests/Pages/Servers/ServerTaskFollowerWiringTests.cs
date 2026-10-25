// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R3-37: the server pages beyond RKHunter and Services (Apache, PortSentry, Docker, TeamSpeak)
/// follow the task an action launches: the follower appears on the click, binds to the task of that
/// server whose name the backend gives it, and goes away when the launch itself was refused.
/// </summary>
public sealed class ServerTaskFollowerWiringTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerTaskFollowerWiringTests() => _handler = BunitTestHelper.RegisterServices(this);

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private static ServerDetailDto Server() => new()
    {
        Id = 30,
        Name = "srv",
        Hostname = "10.0.0.3",
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Docker = new DockerDataDto
        {
            Containers = [],
            Images = [],
            ComposeStacks = [new DockerComposeStackDto { Name = "monitoring", Status = "running(3)", RunningCount = 3, TotalCount = 3 }],
            Networks = [],
            Volumes = []
        },
        Apache = new ApacheDataDto { IsInstalled = true, IsRunning = true, Modules = [], VirtualHosts = [] }
    };

    private Task QueueAsync<T>(IRenderedComponent<T> cut, int id, string name) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.InvokeAsync(() => Tracker.Upsert(new ServerTaskDto
        {
            Id = id,
            ServerId = 30,
            Name = name,
            Status = TaskExecutionStatus.Pending
        }));

    private async Task<ServerDetailLoader> LoaderAsync()
    {
        var loader = new ServerDetailLoader(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<HubConnectionFactory>(),
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance);
        _handler.SetJsonResponse("api/servers/30", Server());
        _handler.SetJsonResponse("api/monitoring/servers/30/metrics", new List<ServerMetricDto>());
        await loader.EnsureLoadedAsync(30, Xunit.TestContext.Current.CancellationToken);
        return loader;
    }

    [Fact]
    public async Task Apache_TestConfig_IsFollowedFromTheClickToItsTask()
    {
        _handler.SetJsonResponse("api/servers/30/apache/action", "{}");
        var cut = this.RenderWithPageHeader<ServerApacheSection>(p => p.Add(x => x.Server, Server()).Add(x => x.ServerId, 30));

        await cut.InvokeAsync(() => cut.FindMenuItem("TestConfig")!.Click());
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskRequested", cut.Find(".server-task-follower").TextContent));

        await QueueAsync(cut, 61001, "Apache TestConfig");
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskQueued", cut.Find(".server-task-follower").TextContent));
        Assert.Contains("/tasks/61001", cut.Markup);
    }

    [Fact]
    public async Task Apache_ARefusedLaunch_LeavesNothingToFollow()
    {
        _handler.SetResponse("api/servers/30/apache/action", System.Net.HttpStatusCode.InternalServerError);
        var cut = this.RenderWithPageHeader<ServerApacheSection>(p => p.Add(x => x.Server, Server()).Add(x => x.ServerId, 30));

        await cut.InvokeAsync(() => cut.FindMenuItem("TestConfig")!.Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".server-task-follower")));
    }

    [Fact]
    public async Task Portsentry_ViewLogs_IsFollowedToItsTask()
    {
        var loader = await LoaderAsync();
        _handler.SetPaginatedJsonResponse("api/servers/30/portsentry/blocked", new List<PortsentryBlockedIpDto>());
        _handler.SetPaginatedJsonResponse("api/servers/30/portsentry/whitelist", new List<PortsentryWhitelistIpDto>());
        _handler.SetJsonResponse("api/servers/30/portsentry/filter-values", new PortsentryFilterValuesDto());
        _handler.SetJsonResponse("api/servers/30/portsentry/logs", "{}");
        var cut = this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 30)
            .Add(x => x.Ps, new PortsentryDataDto { IsInstalled = true, IsRunning = true })
            .AddCascadingValue("ServerLoader", loader));

        await cut.InvokeAsync(() => cut.FindMenuItem("ViewLogs")!.Click());
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskRequested", cut.Find(".server-task-follower").TextContent));

        // Another module's task on the same server is not the one this page launched.
        await QueueAsync(cut, 61002, "Apache logs - error");
        Assert.DoesNotContain("/tasks/61002", cut.Markup);
        await QueueAsync(cut, 61003, "PortSentry - logs");
        cut.WaitForAssertion(() => Assert.Contains("/tasks/61003", cut.Markup));
    }

    [Fact]
    public async Task DockerCompose_Restart_IsFollowedToItsTask()
    {
        var loader = await LoaderAsync();
        _handler.SetJsonResponse("api/servers/30/docker/compose/action", "{}");
        var cut = Render<DockerComposeTab>(p => p
            .Add(x => x.ServerId, 30).Add(x => x.CanAdmin, true).Add(x => x.Stacks, Server().Docker.ComposeStacks)
            .AddCascadingValue("ServerLoader", loader));

        // Restart sits in the stack row's "More actions" menu (STD-BTN detection 11).
        await cut.InvokeAsync(() => cut.Find(".omni-overflow-menu__trigger").Click());
        await cut.InvokeAsync(() => cut.Find(".omni-menu__item[title='Restart']").Click());
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskRequested", cut.Find(".server-task-follower").TextContent));

        await QueueAsync(cut, 61004, "DockerRestart");
        cut.WaitForAssertion(() => Assert.Contains("/tasks/61004", cut.Markup));
    }

    [Fact]
    public async Task TeamspeakOperations_TellTheFollower_AndForgetARefusedOne()
    {
        var follower = Render<ServerTaskFollower>(p => p.Add(x => x.ServerId, 30).Add(x => x.NamePrefix, "TeamSpeak"));
        var runner = new TeamspeakOperationRunner(Services.GetRequiredService<NotifyHelper>(), () => false, () => follower.Instance);

        await follower.InvokeAsync(() => runner.RunOperationAsync(() => Task.FromResult(new ApiStatus(true, false, false)), "Restart"));
        Assert.Equal(ServerTaskFollowState.Requested, follower.Instance.State);
        await QueueAsync(follower, 61005, "TeamSpeak - Restart");
        follower.WaitForAssertion(() => Assert.Equal(61005, follower.Instance.TaskId));

        await follower.InvokeAsync(() => runner.RunOperationAsync(() => Task.FromResult(new ApiStatus(false, false, false)), "Stop"));
        Assert.Equal(ServerTaskFollowState.Idle, follower.Instance.State);
    }

    private static ServerDetailDto CertbotServer() =>
        Server() with { Certbot = new CertbotDataDto { IsInstalled = true, Certificates = [] } };

    [Fact]
    public async Task Certbot_RenewalCheck_IsFollowedFromTheClickToItsTask()
    {
        _handler.SetJsonResponse("api/servers/30/certbot/action", "{}");
        var cut = this.RenderWithPageHeader<ServerCertbotSection>(p => p.Add(x => x.Server, CertbotServer()).Add(x => x.ServerId, 30));

        await cut.InvokeAsync(() => cut.FindMenuItem("CertbotRenewalCheck")!.Click());
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskRequested", cut.Find(".server-task-follower").TextContent));

        await QueueAsync(cut, 61006, "Certbot RenewalCheck");
        cut.WaitForAssertion(() => Assert.Contains("/tasks/61006", cut.Markup));
    }

    [Fact]
    public async Task Certbot_ARefusedLaunch_LeavesNothingToFollow()
    {
        _handler.SetResponse("api/servers/30/certbot/action", System.Net.HttpStatusCode.InternalServerError);
        var cut = this.RenderWithPageHeader<ServerCertbotSection>(p => p.Add(x => x.Server, CertbotServer()).Add(x => x.ServerId, 30));

        await cut.InvokeAsync(() => cut.FindMenuItem("CertbotRenewalCheck")!.Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".server-task-follower")));
    }
}
