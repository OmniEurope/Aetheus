// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.Repositories;

public class ProjectRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ProjectRepository _repo;

    public ProjectRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        var analysisGrades = Substitute.For<IProjectAnalysisGradeReader>();
        analysisGrades.GetGradesAsync(
                Arg.Any<IReadOnlyCollection<int>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new Aetheus.Back.Components.Analysis.AnalysisProjectSummaryRepository(_db)
                .GetGradesAsync(
                    call.Arg<IReadOnlyCollection<int>>(),
                    call.ArgAt<CancellationToken>(1)));
        _repo = new ProjectRepository(_db, analysisGrades);
    }

    [Fact]
    public async Task GetProjectsPagedAsync_ReturnsPagedDefaultOrderByName()
    {
        _db.Projects.AddRange(
            new Project { Name = "Zeta", Description = "d" },
            new Project { Name = "Alpha", Description = "d" },
            new Project { Name = "Mid", Description = "d" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetProjectsPagedAsync(null, null, false, 1, 2, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, total);
        Assert.Equal(2, items.Count);
        Assert.Equal("Alpha", items[0].Name);
    }

    [Fact]
    public async Task GetProjectsPagedAsync_GivesThePipelineCountAndTheLastRun_WithoutThePipelineYaml()
    {
        // The tile needs a count and the latest run, not the pipelines' definitions.
        var project = new Project { Name = "Aetheus", Description = "d" };
        var older = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
        project.Pipelines.Add(new Pipeline
        {
            Name = "candidate",
            YamlDefinition = "name: candidate",
            Runs =
            [
                new PipelineRun { Status = PipelineStatus.Failed, StartedAt = older },
                new PipelineRun { Status = PipelineStatus.Success, StartedAt = older.AddHours(2) }
            ]
        });
        project.Pipelines.Add(new Pipeline { Name = "nightly", YamlDefinition = "name: nightly" });
        _db.Projects.AddRange(project, new Project { Name = "Empty", Description = "d" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetProjectsPagedAsync(null, null, false, 1, 10, ct: TestContext.Current.CancellationToken);

        var dto = Aetheus.Back.Components.Shared.ProjectDtoMapper.ToDto(items.Single(p => p.Name == "Aetheus"), includePipelineSummary: true);
        Assert.Equal(2, dto.PipelineCount);
        Assert.Equal(PipelineStatus.Success, dto.LastRunStatus);
        Assert.Equal(older.AddHours(2), dto.LastRunAt);
        Assert.All(items.SelectMany(p => p.Pipelines), pipeline => Assert.Equal(string.Empty, pipeline.YamlDefinition));
        Assert.Empty(items.Single(p => p.Name == "Empty").Pipelines);
    }

    [Fact]
    public async Task GetProjectsPagedAsync_WithSearch_Filters()
    {
        _db.Projects.AddRange(
            new Project { Name = "Frontend", Description = "React app" },
            new Project { Name = "Backend", Description = "API" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetProjectsPagedAsync("Front", null, false, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetProjectsPagedAsync_WithStatus_Filters()
    {
        _db.Projects.AddRange(
            new Project { Name = "Active", Description = "d", Status = ProjectStatus.Active },
            new Project { Name = "Archived", Description = "d", Status = ProjectStatus.Archived }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetProjectsPagedAsync(null, null, false, 1, 10, ProjectStatus.Active, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetProjectsPagedAsync_WithAccessibleIds_Filters()
    {
        _db.Projects.AddRange(
            new Project { Name = "P1", Description = "d" },
            new Project { Name = "P2", Description = "d" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _db.Projects.Select(p => p.Id).Take(1).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        var (items, total) = await _repo.GetProjectsPagedAsync(null, null, false, 1, 10, accessibleIds: ids, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetProjectsPagedAsync_SortByNameDescending()
    {
        _db.Projects.AddRange(
            new Project { Name = "Alpha", Description = "d" },
            new Project { Name = "Zeta", Description = "d" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetProjectsPagedAsync(null, "name", true, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal("Zeta", items[0].Name);
    }

    [Fact]
    public async Task GetProjectsPagedAsync_SortByCreatedAt()
    {
        _db.Projects.AddRange(
            new Project { Name = "Old", Description = "d", CreatedAt = DateTime.UtcNow.AddDays(-1) },
            new Project { Name = "New", Description = "d", CreatedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetProjectsPagedAsync(null, "createdat", false, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal("Old", items[0].Name);
    }

    [Fact]
    public async Task GetProjectsPagedAsync_SortByStatus()
    {
        _db.Projects.AddRange(
            new Project { Name = "P1", Description = "d", Status = ProjectStatus.Archived },
            new Project { Name = "P2", Description = "d", Status = ProjectStatus.Active }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetProjectsPagedAsync(null, "status", false, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetProjectDetailAsync_Found_IncludesPipelines()
    {
        var project = new Project { Name = "P", Description = "d" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.Pipelines.Add(new Pipeline { ProjectId = project.Id, Name = "Build", YamlDefinition = "name: Build" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetProjectDetailAsync(project.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Single(result.Pipelines);
    }

    [Fact]
    public async Task GetProjectDetailAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.GetProjectDetailAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveRunStepLabelsAsync_PrefersRunningStep_AndNormalizesStage()
    {
        var project = new Project { Name = "P", Description = "d" };
        var pipeline = new Pipeline { Name = "CI", Project = project };
        var run = new PipelineRun { Pipeline = pipeline, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        run.StepRuns =
        [
            new PipelineStepRun { PipelineRun = run, StageName = "Build", StepName = "Pending", Status = TaskExecutionStatus.Pending, Order = 1 },
            new PipelineStepRun { PipelineRun = run, StageName = "System:QA", StepName = "Tests", Status = TaskExecutionStatus.Running, Order = 2 }
        ];
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var labels = await _repo.GetActiveRunStepLabelsAsync(project.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal("QA · Tests", labels[run.Id]);
    }

    [Fact]
    public async Task FindProjectWithPipelinesAsync_Found()
    {
        var project = new Project { Name = "P", Description = "d" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindProjectWithPipelinesAsync(project.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindProjectAsync_Found()
    {
        var project = new Project { Name = "P", Description = "d" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindProjectAsync(project.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task AddProjectAsync_Persists()
    {
        await _repo.AddProjectAsync(new Project { Name = "New", Description = "d" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Projects.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAllProjectsWithRepoUrlAsync_ReturnsOnlyWithRepoUrl()
    {
        _db.Projects.AddRange(
            new Project { Name = "WithUrl", Description = "d", RepositoryUrl = "https://github.com/org/repo" },
            new Project { Name = "NoUrl", Description = "d", RepositoryUrl = null },
            new Project { Name = "EmptyUrl", Description = "d", RepositoryUrl = "" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetAllProjectsWithRepoUrlAsync(ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
    }

    [Fact]
    public async Task SaveChangesAsync_PersistsPendingChanges()
    {
        _db.Projects.Add(new Project { Name = "P", Description = "d" });
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Projects.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<(Project project, Pipeline pipeline, PipelineRun run, Server server)> SetupProjectWithPipelineRunAsync()
    {
        var project = new Project { Name = "P1", Description = "d" };
        _db.Projects.Add(project);
        var server = new Server { Name = "srv", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync();

        var pipeline = new Pipeline { Name = "pipe", ProjectId = project.Id, YamlDefinition = "" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync();

        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync();

        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, ServerId = server.Id, StepName = "s1", Status = TaskExecutionStatus.Pending });
        await _db.SaveChangesAsync();

        return (project, pipeline, run, server);
    }

    [Fact]
    public async Task GetServersForProjectAsync_ReturnsServers()
    {
        var (project, _, _, server) = await SetupProjectWithPipelineRunAsync();

        var result = await _repo.GetServersForProjectAsync(project.Id, ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal(server.Name, result[0].Name);
    }

    [Fact]
    public async Task GetProjectServersPageAsync_ReturnsSecondPageAndTotal()
    {
        var project = new Project { Name = "Paged", Description = "d" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ProjectServers.AddRange(Enumerable.Range(0, 205).Select(index => new ProjectServer
        {
            ProjectId = project.Id,
            Type = ProjectServerType.ExternalHost,
            DisplayName = $"Host-{index:D3}",
            Host = $"host-{index:D3}.example"
        }));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, totalCount) = await _repo.GetProjectServersPageAsync(
            project.Id, null, "DisplayName", false, 2, 200, ct: TestContext.Current.CancellationToken);

        Assert.Equal(205, totalCount);
        Assert.Equal(5, items.Count);
        Assert.Equal("Host-200", items[0].DisplayName);
    }

    [Fact]
    public async Task GetTasksPagedAsync_ReturnsPaged()
    {
        var (project, _, run, server) = await SetupProjectWithPipelineRunAsync();

        for (var i = 0; i < 5; i++)
            _db.Tasks.Add(new ServerTask { ServerId = server.Id, Name = $"t{i}", Command = "echo", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending, PipelineRunId = run.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetTasksPagedAsync(project.Id, null, null, false, 1, 3, ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, total);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task GetTasksPagedAsync_AppliesColumnFilters_AndFilterValuesListTheServers()
    {
        // The project tasks section sends real column filters, applied before the count.
        var (project, _, run, server) = await SetupProjectWithPipelineRunAsync();
        _db.Tasks.AddRange(
            new ServerTask { ServerId = server.Id, Name = "build", Command = "echo", Status = TaskExecutionStatus.Failed, PipelineRunId = run.Id },
            new ServerTask { ServerId = server.Id, Name = "test", Command = "echo", Status = TaskExecutionStatus.Success, PipelineRunId = run.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetTasksPagedAsync(project.Id, null, null, false, 1, 10,
            ct: TestContext.Current.CancellationToken,
            columnFilters:
            [
                new GridFilter { Field = "ServerName", Operator = GridFilterOperator.In, Value = server.Name },
                new GridFilter { Field = "Status", Operator = GridFilterOperator.In, Value = "Failed" }
            ]);
        var values = await _repo.GetTaskFilterValuesAsync(project.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
        Assert.Equal("build", Assert.Single(items).Name);
        Assert.Equal([server.Name], values.ServerNames);
    }

    [Fact]
    public async Task GetTasksPagedAsync_ReturnsRequestedSecondPage()
    {
        var (project, _, run, server) = await SetupProjectWithPipelineRunAsync();
        for (var i = 0; i < 30; i++)
            _db.Tasks.Add(new ServerTask
            {
                ServerId = server.Id,
                Name = $"Task-{i:D2}",
                Command = "echo",
                Executor = ExecutorType.Shell,
                Status = TaskExecutionStatus.Pending,
                PipelineRunId = run.Id,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(i)
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, totalCount) = await _repo.GetTasksPagedAsync(
            project.Id, null, "CreatedAt", false, 2, 25, ct: TestContext.Current.CancellationToken);

        Assert.Equal(30, totalCount);
        Assert.Equal(5, items.Count);
        Assert.Equal("Task-25", items[0].Name);
    }

    [Fact]
    public async Task GetLogsPagedAsync_ReturnsPaged()
    {
        var (project, _, run, server) = await SetupProjectWithPipelineRunAsync();

        var task = new ServerTask { ServerId = server.Id, Name = "t", Command = "echo", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending, PipelineRunId = run.Id };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
            _db.TaskLogs.Add(new TaskLog { TaskId = task.Id, Message = $"log{i}", Timestamp = DateTime.UtcNow.AddMinutes(-i) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetLogsPagedAsync(project.Id, null, null, false, 1, 3, ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, total);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task GetLogsPagedAsync_ReturnsRequestedSecondPage()
    {
        var (project, _, run, server) = await SetupProjectWithPipelineRunAsync();
        var task = new ServerTask
        {
            ServerId = server.Id,
            Name = "logs",
            Command = "echo",
            Executor = ExecutorType.Shell,
            Status = TaskExecutionStatus.Pending,
            PipelineRunId = run.Id
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 30; i++)
            _db.TaskLogs.Add(new TaskLog
            {
                TaskId = task.Id,
                Message = $"Log-{i:D2}",
                Timestamp = new DateTime(2026, 1, 1).AddMinutes(i)
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, totalCount) = await _repo.GetLogsPagedAsync(
            project.Id, null, "Timestamp", false, 2, 25, ct: TestContext.Current.CancellationToken);

        Assert.Equal(30, totalCount);
        Assert.Equal(5, items.Count);
        Assert.Equal("Log-25", items[0].Message);
    }

    [Fact]
    public async Task GetLogsPagedAsync_AppliesColumnFilters_BeforeTheCount()
    {
        // The project logs section sends the Level list, the Time range and the Message text as
        // real column filters, applied inside the project's scope and before the count.
        var (project, _, run, server) = await SetupProjectWithPipelineRunAsync();
        var task = new ServerTask { ServerId = server.Id, Name = "t", Command = "echo", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending, PipelineRunId = run.Id };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var at = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var logs = new[]
        {
            new TaskLog { TaskId = task.Id, Level = TaskLogLevel.Error, Message = "Deploy FAILED" },
            new TaskLog { TaskId = task.Id, Level = TaskLogLevel.Error, Message = "Deploy failed" },
            new TaskLog { TaskId = task.Id, Level = TaskLogLevel.Info, Message = "Deploy failed" },
            new TaskLog { TaskId = task.Id, Level = TaskLogLevel.Warning, Message = "Disk low" }
        };
        _db.TaskLogs.AddRange(logs);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        // The context stamps Timestamp on insert; the times under test are set by an update.
        logs[0].Timestamp = at.AddMinutes(30);
        logs[1].Timestamp = at.AddHours(5);
        logs[2].Timestamp = at.AddMinutes(40);
        logs[3].Timestamp = at.AddMinutes(50);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetLogsPagedAsync(project.Id, null, null, false, 1, 10,
            ct: TestContext.Current.CancellationToken,
            columnFilters:
            [
                new GridFilter { Field = "Level", Operator = GridFilterOperator.In, Value = $"Error{GridFilter.ListSeparator}Warning" },
                new GridFilter
                {
                    Field = "Timestamp", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-01T08:00:00Z",
                    SecondOperator = GridFilterOperator.LessThan, SecondValue = "2026-09-01T12:00:00Z"
                },
                new GridFilter { Field = "Message", Operator = GridFilterOperator.Contains, Value = "failed" }
            ]);

        Assert.Equal(1, total);
        Assert.Equal("Deploy FAILED", Assert.Single(items).Message);
    }

    [Fact]
    public async Task GetProjectServersPageAsync_AppliesColumnFilters_BeforeTheCount()
    {
        // The project servers section sends the Type list, the Name and Host texts and the Port
        // number as real column filters; another project's server never leaks in.
        var project = new Project { Name = "Filtered", Description = "d" };
        var other = new Project { Name = "Other", Description = "d" };
        _db.Projects.AddRange(project, other);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ProjectServers.AddRange(
            new ProjectServer { ProjectId = project.Id, Type = ProjectServerType.ExternalHost, DisplayName = "Web front", Host = "10.0.0.1", Port = 22 },
            new ProjectServer { ProjectId = project.Id, Type = ProjectServerType.ExternalHost, DisplayName = "Web back", Host = "10.0.0.2", Port = 2222 },
            new ProjectServer { ProjectId = project.Id, Type = ProjectServerType.AgentServer, DisplayName = "Web agent", Host = "10.0.0.3", Port = 22 },
            new ProjectServer { ProjectId = other.Id, Type = ProjectServerType.ExternalHost, DisplayName = "Web other", Host = "10.0.0.4", Port = 22 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, totalCount) = await _repo.GetProjectServersPageAsync(
            project.Id, null, null, false, 1, 10, ct: TestContext.Current.CancellationToken,
            columnFilters:
            [
                new GridFilter { Field = "Type", Operator = GridFilterOperator.In, Value = "ExternalHost" },
                new GridFilter { Field = "DisplayName", Operator = GridFilterOperator.Contains, Value = "WEB" },
                new GridFilter { Field = "Host", Operator = GridFilterOperator.StartsWith, Value = "10.0" },
                new GridFilter { Field = "Port", Operator = GridFilterOperator.Equals, Value = "22" }
            ]);

        Assert.Equal(1, totalCount);
        Assert.Equal("Web front", Assert.Single(items).DisplayName);
    }

    [Fact]
    public async Task GetRecentTasksAsync_ReturnsLimited()
    {
        var (project, _, run, server) = await SetupProjectWithPipelineRunAsync();

        for (var i = 0; i < 5; i++)
            _db.Tasks.Add(new ServerTask { ServerId = server.Id, Name = $"t{i}", Command = "echo", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending, PipelineRunId = run.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-i) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRecentTasksAsync(project.Id, 3, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetRecentPipelineRunsAsync_ReturnsLimited()
    {
        var (project, pipeline, _, _) = await SetupProjectWithPipelineRunAsync();

        for (var i = 0; i < 4; i++)
            _db.PipelineRuns.Add(new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow.AddMinutes(-i) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRecentPipelineRunsAsync(project.Id, 3, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetProjectListInsightsAsync_NamesTheRootRunNotItsNewestChild_WithCommitProductionAndUsers()
    {
        var now = new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc);
        var project = new Project { Name = "Portfolio", Description = "d" };
        var pipeline = new Pipeline { Name = "Deploy", Project = project };
        var parentRun = new PipelineRun
        {
            Pipeline = pipeline,
            Status = PipelineStatus.Running,
            StartedAt = now.AddMinutes(-10)
        };
        var childRun = new PipelineRun
        {
            Pipeline = pipeline,
            Status = PipelineStatus.Success,
            StartedAt = now.AddMinutes(-5)
        };
        parentRun.StepRuns =
        [
            new PipelineStepRun
            {
                PipelineRun = parentRun,
                StageName = "Deploy",
                StepName = "Child",
                Status = TaskExecutionStatus.Success,
                TriggeredRunId = null
            }
        ];
        _db.PipelineRuns.AddRange(parentRun, childRun);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        parentRun.StepRuns[0].TriggeredRunId = childRun.Id;

        _db.GitCommits.AddRange(
            new GitCommit
            {
                Project = project,
                Sha = "old",
                Message = "Old",
                CommittedAt = now.AddHours(-1),
                CreatedAt = now.AddHours(-1)
            },
            new GitCommit
            {
                Project = project,
                Sha = "0123456789abcdef",
                Message = "Latest",
                CommittedAt = now.AddMinutes(-3),
                CreatedAt = now.AddMinutes(-3)
            });
        var production = new Aetheus.Back.Data.Entities.Environment
        {
            Name = "Production",
            Project = project,
            Type = EnvironmentType.Production
        };
        var app = new MonitoredApp
        {
            Project = project,
            Environment = production,
            Name = "Web",
            Enabled = true,
            CurrentStatus = AppHealthStatus.Up,
            AnalyticsEnabled = true
        };
        _db.MonitoredApps.Add(app);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.AppAnalyticsSessions.Add(new AppAnalyticsSession
        {
            MonitoredAppId = app.Id,
            SessionPseudonym = "session-1",
            StartedAtUtc = now.AddMinutes(-4),
            LastSeenAtUtc = now.AddMinutes(-1)
        });
        var qualityReport = new AnalysisReport
        {
            ProjectId = project.Id,
            PipelineRunId = parentRun.Id,
            ScannerKey = "quality",
            ScannerName = "Quality",
            ScannerVersion = "1",
            ContentHash = "grade-f",
            StartedAt = now.AddMinutes(-3),
            CompletedAt = now.AddMinutes(-2),
            CreatedAt = now.AddMinutes(-3)
        };
        var securityReport = new AnalysisReport
        {
            ProjectId = project.Id,
            PipelineRunId = childRun.Id,
            ScannerKey = "security",
            ScannerName = "Security",
            ScannerVersion = "1",
            ContentHash = "grade-a",
            StartedAt = now.AddMinutes(-2),
            CompletedAt = now.AddMinutes(-1),
            CreatedAt = now.AddMinutes(-2)
        };
        _db.AnalysisReports.AddRange(qualityReport, securityReport);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var qualitySnapshot = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.F,
            Completeness = AnalysisGradeCompleteness.Complete,
            EvaluatedAt = now.AddMinutes(-2),
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.CodeQuality,
                    Grade = AnalysisGrade.F,
                    Required = true,
                    Completeness = AnalysisGradeCompleteness.Complete,
                    EvaluatedAt = now.AddMinutes(-2),
                    Measures =
                    [
                        new AnalysisGradeMeasureDto
                        {
                            Key = "quality",
                            Domain = AnalysisGradeDomain.CodeQuality,
                            Grade = AnalysisGrade.F,
                            Required = true,
                            Observed = true,
                            ObservedValue = 40,
                            Direction = AnalysisMetricDirection.HigherIsBetter,
                            AThreshold = 90,
                            BThreshold = 80,
                            CThreshold = 70,
                            DThreshold = 60,
                            EThreshold = 50
                        }
                    ]
                }
            ]
        };
        var securitySnapshot = new AnalysisGradeSummaryDto
        {
            EvaluatedAt = now.AddMinutes(-1),
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.Security,
                    Grade = AnalysisGrade.A,
                    Required = true,
                    Completeness = AnalysisGradeCompleteness.Complete,
                    EvaluatedAt = now.AddMinutes(-1),
                    Measures =
                    [
                        new AnalysisGradeMeasureDto
                        {
                            Key = "security",
                            Domain = AnalysisGradeDomain.Security,
                            Grade = AnalysisGrade.A,
                            Required = true,
                            Observed = true,
                            ObservedValue = 100,
                            Direction = AnalysisMetricDirection.HigherIsBetter,
                            AThreshold = 90,
                            BThreshold = 80,
                            CThreshold = 70,
                            DThreshold = 60,
                            EThreshold = 50
                        }
                    ]
                }
            ]
        };
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _db.AnalysisEvaluations.AddRange(
            new AnalysisEvaluation
            {
                ProjectId = project.Id,
                AnalysisReportId = qualityReport.Id,
                PipelineRunId = parentRun.Id,
                Status = AnalysisGateStatus.Blocked,
                Grade = AnalysisGrade.F,
                GradeCompleteness = AnalysisGradeCompleteness.Complete,
                GradeSnapshotJson = JsonSerializer.Serialize(qualitySnapshot, jsonOptions),
                EvaluatedAt = now.AddMinutes(-2),
                CreatedAt = now.AddMinutes(-2)
            },
            new AnalysisEvaluation
            {
                ProjectId = project.Id,
                AnalysisReportId = securityReport.Id,
                PipelineRunId = childRun.Id,
                Status = AnalysisGateStatus.Passed,
                Grade = AnalysisGrade.A,
                GradeCompleteness = AnalysisGradeCompleteness.Complete,
                GradeSnapshotJson = JsonSerializer.Serialize(securitySnapshot, jsonOptions),
                EvaluatedAt = now.AddMinutes(-1),
                CreatedAt = now.AddMinutes(-1)
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetProjectListInsightsAsync(
            [project.Id], now.AddMinutes(-5), TestContext.Current.CancellationToken);

        var insight = Assert.Single(result).Value;
        Assert.Equal("0123456789abcdef", insight.LastCommitSha);
        // Select the parent root run even when a triggered child is newer.
        Assert.Equal(parentRun.Id, insight.LastRunId);
        Assert.NotEqual(childRun.Id, insight.LastRunId);
        // And its status is the parent's: a green child under a parent still running is not "done".
        Assert.Equal(PipelineStatus.Running, insight.LastRunStatus);
        Assert.Equal(AnalysisGrade.F, insight.LatestGateGrade);
        Assert.Equal(ProjectProductionStatus.Online, insight.ProductionStatus);
        Assert.Equal(1, insight.OnlineUserCount);
    }


    [Fact]
    public async Task GetProjectSectionCountsAsync_CountsAServerCarriedByAnEnvironment_ApartFromDirectAttachments()
    {
        // Count environment-linked servers separately from direct attachments so readiness sees
        // both while section counts match their rows.
        var project = new Project { Name = "API", Description = "d" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var environment = new Aetheus.Back.Data.Entities.Environment { Name = "qa", ProjectId = project.Id };
        var other = new Aetheus.Back.Data.Entities.Environment { Name = "prod", ProjectId = project.Id };
        var foreign = new Aetheus.Back.Data.Entities.Environment { Name = "elsewhere", ProjectId = null };
        _db.Environments.AddRange(environment, other, foreign);
        var server = new Server { Name = "vps1", Hostname = "vps1" };
        var unrelated = new Server { Name = "vps2", Hostname = "vps2" };
        _db.Servers.AddRange(server, unrelated);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.EnvironmentServers.AddRange(
            // The same server on two environments of the project is still one server.
            new EnvironmentServer { EnvironmentId = environment.Id, ServerId = server.Id },
            new EnvironmentServer { EnvironmentId = other.Id, ServerId = server.Id },
            // A server reached through an environment of no project must not be counted here.
            new EnvironmentServer { EnvironmentId = foreign.Id, ServerId = unrelated.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var counts = await _repo.GetProjectSectionCountsAsync(project.Id, TestContext.Current.CancellationToken);

        Assert.Equal(0, counts.Servers);
        Assert.Equal(1, counts.EnvironmentServers);
    }

    private static readonly DateTime TileNow = new(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);

    private static PipelineRun TileRun(Pipeline pipeline, PipelineStatus status, int minutesAgo, params PipelineStepRun[] steps)
    {
        var run = new PipelineRun { Pipeline = pipeline, Status = status, StartedAt = TileNow.AddMinutes(-minutesAgo) };
        foreach (var step in steps) step.PipelineRun = run;
        run.StepRuns = [.. steps];
        return run;
    }

    private static PipelineStepRun TileStep(string stage, string step, TaskExecutionStatus status, int order, bool system = false) =>
        new() { StageName = stage, StepName = step, Status = status, Order = order, IsSystem = system };

    private async Task<ProjectListInsight> TileInsightAsync(Project project) =>
        (await _repo.GetProjectListInsightsAsync([project.Id], TileNow.AddMinutes(-5), TestContext.Current.CancellationToken))[project.Id];

    /// <summary>A Running root takes precedence over a newer finished root and includes its active
    /// step.</summary>
    [Fact]
    public async Task GetProjectListInsightsAsync_AnOlderRunningRootWinsOverANewerFinishedOne_WithItsStep()
    {
        var project = new Project { Name = "App", Description = "d" };
        var deploy = new Pipeline { Name = "Deploy", Project = project };
        var lint = new Pipeline { Name = "Lint", Project = project };
        var active = TileRun(deploy, PipelineStatus.Running, 30,
            TileStep("Build", "compile", TaskExecutionStatus.Success, 1),
            TileStep("Deploy", "push", TaskExecutionStatus.Running, 2),
            TileStep("Deploy", "smoke", TaskExecutionStatus.Pending, 3));
        var finished = TileRun(lint, PipelineStatus.Success, 5);
        _db.PipelineRuns.AddRange(active, finished);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var insight = await TileInsightAsync(project);

        Assert.Equal(active.Id, insight.LastRunId);
        Assert.Equal("Deploy", insight.LastRunName);
        Assert.True(insight.LastRunIsActive);
        Assert.Equal("Deploy · push", insight.LastRunCurrentStep);
    }

    /// <summary>With two independent roots running, the most recently started; its next pending
    /// non-system step names it when nothing runs yet.</summary>
    [Fact]
    public async Task GetProjectListInsightsAsync_TwoRunningRoots_NameTheNewestAndItsNextPendingStep()
    {
        var project = new Project { Name = "App", Description = "d" };
        var first = new Pipeline { Name = "First", Project = project };
        var second = new Pipeline { Name = "Second", Project = project };
        var older = TileRun(first, PipelineStatus.Running, 20, TileStep("A", "a", TaskExecutionStatus.Running, 1));
        var newer = TileRun(second, PipelineStatus.Running, 10,
            TileStep("System", "checkout", TaskExecutionStatus.Pending, 1, system: true),
            TileStep("Test", "unit", TaskExecutionStatus.Pending, 2));
        _db.PipelineRuns.AddRange(older, newer);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var insight = await TileInsightAsync(project);

        Assert.Equal(newer.Id, insight.LastRunId);
        Assert.Equal("Test · unit", insight.LastRunCurrentStep);
    }

    /// <summary>A running child under a finished root does not make the tile "in progress": the tile
    /// names the root (D24), which has ended.</summary>
    [Fact]
    public async Task GetProjectListInsightsAsync_ARunningChildUnderAFinishedRoot_NamesTheRootAsEnded()
    {
        var project = new Project { Name = "App", Description = "d" };
        var pipeline = new Pipeline { Name = "Release", Project = project };
        var trigger = TileStep("Deploy", "child", TaskExecutionStatus.Success, 1);
        var root = TileRun(pipeline, PipelineStatus.Success, 30, trigger);
        var child = TileRun(pipeline, PipelineStatus.Running, 20, TileStep("X", "x", TaskExecutionStatus.Running, 1));
        _db.PipelineRuns.AddRange(root, child);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        trigger.TriggeredRunId = child.Id;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var insight = await TileInsightAsync(project);

        Assert.Equal(root.Id, insight.LastRunId);
        Assert.False(insight.LastRunIsActive);
        Assert.Null(insight.LastRunCurrentStep);
    }

    /// <summary>Without a Running root, the newest root, whatever its status: Pending and
    /// WaitingForApproval are not "in progress" (D45), they show as the last run with their badge.</summary>
    [Fact]
    public async Task GetProjectListInsightsAsync_NoRunningRoot_NamesTheNewestAsTheLastRun()
    {
        var project = new Project { Name = "App", Description = "d" };
        var pipeline = new Pipeline { Name = "Deploy", Project = project };
        var failed = TileRun(pipeline, PipelineStatus.Failed, 30);
        var waiting = TileRun(pipeline, PipelineStatus.WaitingForApproval, 10,
            TileStep("Approve", "gate", TaskExecutionStatus.Pending, 1));
        _db.PipelineRuns.AddRange(failed, waiting);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var insight = await TileInsightAsync(project);

        Assert.Equal(waiting.Id, insight.LastRunId);
        Assert.Equal(PipelineStatus.WaitingForApproval, insight.LastRunStatus);
        Assert.False(insight.LastRunIsActive);
        Assert.Null(insight.LastRunCurrentStep);
    }

    public void Dispose() => _db.Dispose();
}
