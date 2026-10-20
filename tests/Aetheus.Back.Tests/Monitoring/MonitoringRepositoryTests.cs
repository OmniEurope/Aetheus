// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

public class MonitoringRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly MonitoringRepository _repo;

    public MonitoringRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new MonitoringRepository(_db);
    }

    [Fact]
    public async Task GetDashboardServersAsync_ReturnsAll()
    {
        _db.Servers.AddRange(
            new Server { Name = "s1", Hostname = "h1" },
            new Server { Name = "s2", Hostname = "h2" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetDashboardServersAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task CountPendingTasksAsync_CountsCorrectly()
    {
        var server = new Server { Name = "s", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Tasks.AddRange(
            new ServerTask { ServerId = server.Id, Name = "t1", Command = "c", Status = TaskExecutionStatus.Pending },
            new ServerTask { ServerId = server.Id, Name = "t2", Command = "c", Status = TaskExecutionStatus.Running },
            new ServerTask { ServerId = server.Id, Name = "t3", Command = "c", Status = TaskExecutionStatus.Pending }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var count = await _repo.CountPendingTasksAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountRunningPipelinesAsync_CountsCorrectly()
    {
        var p = new Pipeline { Name = "p", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow },
            new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow },
            new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var count = await _repo.CountRunningPipelinesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetRecentRunsAsync_ReturnsOrderedByDate()
    {
        var p = new Pipeline { Name = "p", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow.AddHours(-2) },
            new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRecentRunsAsync(5, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.True(result[0].StartedAt > result[1].StartedAt);
    }

    [Fact]
    public async Task DirectPipelineRead_IncludesOnlyThatPipelinesRunsWithoutProjectRead()
    {
        var allowed = new Pipeline { Name = "allowed", YamlDefinition = "y" };
        var denied = new Pipeline { Name = "denied", YamlDefinition = "y" };
        _db.Pipelines.AddRange(allowed, denied);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = allowed.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow },
            new PipelineRun { PipelineId = denied.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runs = await _repo.GetRecentRunsAsync(10, [], [allowed.Id], TestContext.Current.CancellationToken);
        var count = await _repo.CountRunningPipelinesAsync([], [allowed.Id], TestContext.Current.CancellationToken);

        Assert.Single(runs);
        Assert.Equal(allowed.Id, runs[0].PipelineId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ScopedDashboardQueries_FailClosedForEmptyListsAndCoverEveryPipelineOwnerPath()
    {
        var allowedProject = new Project { Name = "allowed" };
        var deniedProject = new Project { Name = "denied" };
        _db.Projects.AddRange(allowedProject, deniedProject);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var environment = new Aetheus.Back.Data.Entities.Environment
        {
            Name = "prod",
            ProjectId = allowedProject.Id
        };
        var projectServer = new ProjectServer
        {
            ProjectId = allowedProject.Id,
            DisplayName = "edge",
            Host = "edge.example"
        };
        _db.Environments.Add(environment);
        _db.ProjectServers.Add(projectServer);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var directProject = new Pipeline
        { Name = "project", YamlDefinition = "y", ProjectId = allowedProject.Id };
        var viaEnvironment = new Pipeline
        { Name = "environment", YamlDefinition = "y", EnvironmentId = environment.Id };
        var viaProjectServer = new Pipeline
        { Name = "project-server", YamlDefinition = "y", ProjectServerId = projectServer.Id };
        var directlyGranted = new Pipeline
        { Name = "direct-grant", YamlDefinition = "y", ProjectId = deniedProject.Id };
        var denied = new Pipeline
        { Name = "denied", YamlDefinition = "y", ProjectId = deniedProject.Id };
        _db.Pipelines.AddRange(
            directProject,
            viaEnvironment,
            viaProjectServer,
            directlyGranted,
            denied);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        foreach (var pipeline in new[]
                 { directProject, viaEnvironment, viaProjectServer, directlyGranted, denied })
        {
            _db.PipelineRuns.Add(new PipelineRun
            {
                PipelineId = pipeline.Id,
                Status = PipelineStatus.Running,
                StartedAt = DateTime.UtcNow
            });
        }

        var allowedServer = new Server { Name = "allowed", Hostname = "allowed" };
        var deniedServer = new Server { Name = "denied", Hostname = "denied" };
        _db.Servers.AddRange(allowedServer, deniedServer);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.Tasks.AddRange(
            new ServerTask
            {
                ServerId = allowedServer.Id,
                Name = "allowed",
                Command = "c",
                Status = TaskExecutionStatus.Pending
            },
            new ServerTask
            {
                ServerId = deniedServer.Id,
                Name = "denied",
                Command = "c",
                Status = TaskExecutionStatus.Pending
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(await _repo.GetDashboardServersAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(0, await _repo.CountPendingTasksAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(
            [allowedServer.Id],
            (await _repo.GetDashboardServersAsync(
                [allowedServer.Id],
                TestContext.Current.CancellationToken)).Select(server => server.Id));
        Assert.Equal(
            1,
            await _repo.CountPendingTasksAsync(
                [allowedServer.Id],
                TestContext.Current.CancellationToken));

        Assert.Empty(await _repo.GetRecentProjectsAsync(
            10, [], TestContext.Current.CancellationToken));
        Assert.Equal(
            [allowedProject.Id],
            (await _repo.GetRecentProjectsAsync(
                10,
                [allowedProject.Id],
                TestContext.Current.CancellationToken)).Select(project => project.Id));

        var noAccess = await _repo.GetRecentRunsAsync(
            10, [], [], TestContext.Current.CancellationToken);
        Assert.Empty(noAccess);
        Assert.Equal(
            0,
            await _repo.CountRunningPipelinesAsync(
                [], [], TestContext.Current.CancellationToken));

        var projectAccess = await _repo.GetRecentRunsAsync(
            10,
            [allowedProject.Id],
            [],
            TestContext.Current.CancellationToken);
        Assert.Equal(
            [directProject.Id, viaEnvironment.Id, viaProjectServer.Id],
            projectAccess.Select(run => run.PipelineId).Order());

        var combinedAccess = await _repo.GetRecentRunsAsync(
            10,
            [allowedProject.Id],
            [directlyGranted.Id],
            TestContext.Current.CancellationToken);
        Assert.Equal(
            [directProject.Id, viaEnvironment.Id, viaProjectServer.Id, directlyGranted.Id],
            combinedAccess.Select(run => run.PipelineId).Order());
        Assert.Equal(
            4,
            await _repo.CountRunningPipelinesAsync(
                [allowedProject.Id],
                [directlyGranted.Id],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetServerMetricsSinceAsync_FiltersAndOrders()
    {
        var server = new Server { Name = "s", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var now = DateTime.UtcNow;
        _db.ServerMetrics.AddRange(
            new ServerMetric { ServerId = server.Id, Timestamp = now.AddHours(-5), CpuPercent = 10 },
            new ServerMetric { ServerId = server.Id, Timestamp = now.AddHours(-1), CpuPercent = 20 },
            new ServerMetric { ServerId = server.Id, Timestamp = now, CpuPercent = 30 }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetServerMetricsSinceAsync(server.Id, now.AddHours(-2), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Equal(20, result[0].CpuPercent);
        Assert.Equal(30, result[1].CpuPercent);
    }

    [Fact]
    public async Task GetServerMetricsSinceAsync_AfterCursorAndTakeReturnBoundedLatestWindow()
    {
        var server = new Server { Name = "bounded", Hostname = "bounded-host" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        _db.ServerMetrics.AddRange(
            Enumerable.Range(0, 5).Select(index => new ServerMetric
            {
                ServerId = server.Id,
                Timestamp = now.AddMinutes(index),
                CpuPercent = index
            }));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetServerMetricsSinceAsync(
            server.Id,
            now.AddHours(-1),
            TestContext.Current.CancellationToken,
            afterUtc: now,
            take: 2);

        Assert.Equal([3, 4], result.Select(metric => metric.CpuPercent));
    }

    /// <summary>The tile shows ten groups, so ten roots are read with the runs they triggered
    /// (children and grandchildren), and an older root beyond the ten is not read at all.</summary>
    [Fact]
    public async Task R480_RecentRuns_ReadsTheShownGroupsWithTheirDescendants_AndNoOlderRoot()
    {
        var pipeline = new Pipeline { Name = "p", YamlDefinition = "y" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        var roots = Enumerable.Range(0, 12)
            .Select(index => new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = now.AddMinutes(-index * 10) })
            .ToList();
        var child = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = now.AddMinutes(1) };
        var grandChild = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = now.AddMinutes(2) };
        _db.PipelineRuns.AddRange(roots);
        _db.PipelineRuns.AddRange(child, grandChild);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = roots[3].Id, StageName = "s", StepName = "child", TriggeredRunId = child.Id },
            new PipelineStepRun { PipelineRunId = child.Id, StageName = "s", StepName = "grandchild", TriggeredRunId = grandChild.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRecentRunsAsync(10, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(12, result.Count);
        Assert.Contains(result, run => run.Id == child.Id);
        Assert.Contains(result, run => run.Id == grandChild.Id);
        Assert.DoesNotContain(result, run => run.Id == roots[10].Id || run.Id == roots[11].Id);
        Assert.Equal(result.OrderByDescending(run => run.StartedAt).Select(run => run.Id), result.Select(run => run.Id));
    }

    /// <summary>A run triggered by a run the caller cannot read is a group of its own.</summary>
    [Fact]
    public async Task R480_RecentRuns_AChildOfAnUnreadableParent_IsItsOwnGroup()
    {
        var hidden = new Pipeline { Name = "hidden", YamlDefinition = "y" };
        var shown = new Pipeline { Name = "shown", YamlDefinition = "y" };
        _db.Pipelines.AddRange(hidden, shown);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var parent = new PipelineRun { PipelineId = hidden.Id, StartedAt = DateTime.UtcNow.AddMinutes(-1) };
        var child = new PipelineRun { PipelineId = shown.Id, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.AddRange(parent, child);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = parent.Id, StageName = "s", StepName = "t", TriggeredRunId = child.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRecentRunsAsync(10, [], [shown.Id], TestContext.Current.CancellationToken);

        Assert.Equal([child.Id], result.Select(run => run.Id));
    }

    /// <summary>The server rows come back as the tile's DTO, online first then by
    /// heartbeat.</summary>
    [Fact]
    public async Task R480_DashboardServers_AreProjected_OnlineFirst_ThenMostRecentlyActive()
    {
        var now = DateTime.UtcNow;
        _db.Servers.AddRange(
            new Server { Name = "off", Hostname = "off", Status = ServerStatus.Offline, LastHeartbeat = now, SudoersBaseline = "secret-ish" },
            new Server
            {
                Name = "old",
                Hostname = "old",
                Status = ServerStatus.Online,
                LastHeartbeat = now.AddHours(-1),
                OsDescription = "Ubuntu",
                PipelineRunnerEnabled = true,
                DeploymentTargetAvailable = true,
                PackageManagementAvailable = true,
                CapabilityDiagnosticsJson = "[\"sudo blocked\"]"
            },
            new Server { Name = "new", Hostname = "new", Status = ServerStatus.Online, LastHeartbeat = now },
            new Server { Name = "never", Hostname = "never", Status = ServerStatus.Offline, PipelineRunnerEnabled = true });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetDashboardServersAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(["new", "old", "off", "never"], result.Select(server => server.Name));
        Assert.Equal("Ubuntu", result[1].OsDescription);
        // The tile's capability icons: build, deploy, manage, and the diagnostics tooltip.
        Assert.Equal(((bool?)true, (bool?)true, (bool?)true), (result[1].PipelineRunnerEnabled, result[1].DeploymentTargetAvailable, result[1].PackageManagementAvailable));
        Assert.Equal(["sudo blocked"], result[1].CapabilityDiagnostics);
        Assert.Equal(((bool?)false, (bool?)false, (bool?)false), (result[0].PipelineRunnerEnabled, result[0].DeploymentTargetAvailable, result[0].PackageManagementAvailable));
        // Never phoned home: unknown capabilities (the tile's question mark), not "none".
        Assert.Equal(((bool?)null, (bool?)null, (bool?)null), (result[3].PipelineRunnerEnabled, result[3].DeploymentTargetAvailable, result[3].PackageManagementAvailable));
    }

    public void Dispose() => _db.Dispose();
}
