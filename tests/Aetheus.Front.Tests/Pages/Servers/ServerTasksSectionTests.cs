// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Components.Tasks;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerTasksSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerTasksSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeServer() =>
        new()
        {
            Id = 1,
            Name = "web-server-01",
            Hostname = "web-server-01",
            OsDescription = "Ubuntu 22.04",
            AgentVersion = "1.2.3",
            Status = ServerStatus.Online,
            Type = ServerType.Normal,
            LastHeartbeat = DateTime.UtcNow
        };

    private void StubTasks(params ServerTaskDto[] tasks) =>
        _handler.SetJsonResponse("api/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items = [.. tasks],
            TotalCount = tasks.Length
        });

    // The per-server section now hosts the shared TaskListView, scoped to this server.
    [Fact]
    public void Hosts_TaskListView_ScopedToServer()
    {
        StubTasks();
        var server = MakeServer();
        var cut = Render<ServerTasksSection>(p => p.Add(x => x.Server, server));

        var view = cut.FindComponent<TaskListView>();
        Assert.Equal(server.Id, view.Instance.ServerId);
        Assert.Equal(server.Name, view.Instance.ServerName);
    }

    [Fact]
    public void TaskListServerIdChange_ReloadsSameComponentInstance()
    {
        StubTasks();
        var cut = Render<TaskListView>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Url.Contains("serverId=1", StringComparison.Ordinal)));

        cut.Render(p => p.Add(x => x.ServerId, 2));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Url.Contains("serverId=2", StringComparison.Ordinal)));
    }

    [Fact]
    public void Renders_TasksFromApi()
    {
        StubTasks(
            new ServerTaskDto { Id = 1, Name = "Deploy", ServerStatus = ServerStatus.Online, Status = TaskExecutionStatus.Success, CreatedAt = DateTime.UtcNow.AddHours(-1) },
            new ServerTaskDto { Id = 2, Name = "Test Run", ServerStatus = ServerStatus.Online, Status = TaskExecutionStatus.Failed, CreatedAt = DateTime.UtcNow.AddHours(-2) });

        var cut = Render<ServerTasksSection>(p => p.Add(x => x.Server, MakeServer()));
        cut.WaitForState(() => cut.Markup.Contains("Deploy"));

        Assert.Contains("Deploy", cut.Markup);
        Assert.Contains("Test Run", cut.Markup);
    }

    [Fact]
    public void Renders_TaskName_InMarkup()
    {
        StubTasks(new ServerTaskDto { Id = 10, Name = "DeployAlpha", ServerStatus = ServerStatus.Online, Status = TaskExecutionStatus.Success, CreatedAt = DateTime.UtcNow });

        var cut = Render<ServerTasksSection>(p => p.Add(x => x.Server, MakeServer()));
        cut.WaitForState(() => cut.Markup.Contains("DeployAlpha"));

        Assert.Contains("DeployAlpha", cut.Markup);
    }

    [Fact]
    public void Renders_PipelineAndServerSources()
    {
        StubTasks(
            new ServerTaskDto { Id = 10, ServerId = 1, Name = "Pipeline task", PipelineRunId = 55, Status = TaskExecutionStatus.Success, CreatedAt = DateTime.UtcNow },
            new ServerTaskDto { Id = 11, ServerId = 1, Name = "Manual task", Status = TaskExecutionStatus.Success, CreatedAt = DateTime.UtcNow });

        var cut = Render<ServerTasksSection>(p => p.Add(x => x.Server, MakeServer()));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("a[href='/pipelines/runs/55']"));
            Assert.Contains("web-server-01", cut.Find("a[href='/servers/1/overview']").TextContent);
        });
    }

    [Fact]
    public void ServerParam_IsSet()
    {
        StubTasks();
        var server = MakeServer();
        var cut = Render<ServerTasksSection>(p => p.Add(x => x.Server, server));
        Assert.Equal(server.Id, cut.Instance.Server.Id);
    }

    [Fact]
    public void Renders_RunningTask()
    {
        StubTasks(new ServerTaskDto { Id = 3, Name = "RunningJob", ServerStatus = ServerStatus.Online, Status = TaskExecutionStatus.Running, CreatedAt = DateTime.UtcNow });

        var cut = Render<ServerTasksSection>(p => p.Add(x => x.Server, MakeServer()));
        cut.WaitForState(() => cut.Markup.Contains("RunningJob"));

        Assert.Contains("RunningJob", cut.Markup);
    }
}
