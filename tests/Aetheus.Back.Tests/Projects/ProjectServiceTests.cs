// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ProjectServiceTests
{
    private readonly IProjectRepository _repo = Substitute.For<IProjectRepository>();
    private readonly IServerLifecycleService _serverService = Substitute.For<IServerLifecycleService>();
    private readonly Aetheus.Back.Components.Notifications.IUserNotificationService _userNotifications =
        Substitute.For<Aetheus.Back.Components.Notifications.IUserNotificationService>();
    private readonly ProjectService _sut;

    public ProjectServiceTests()
    {
        var orgService = Substitute.For<IOrganizationService>();
        orgService.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>())
            .Returns(1);
        // No git activity by default; the projects-list test overrides this with a specific date.
        _repo.GetLastGitUpdatesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DateTime?>());
        _repo.GetInternalRepositoryIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int>());
        _repo.GetProjectListInsightsAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, ProjectListInsight>());
        _repo.GetActiveRunStepLabelsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>());
        _sut = new ProjectService(_repo, Substitute.For<IAuditService>(), Substitute.For<IEntityChangeNotifier>(), orgService, _serverService, TimeProvider.System, Substitute.For<IMemoryCache>(), Substitute.For<Aetheus.Back.Components.PortRegistry.IPortRegistryService>(), _userNotifications);
    }

    [Fact]
    public async Task GetProjectsAsync_ReturnsPaginatedResult()
    {
        var projects = new List<Project>
        {
            new() { Id = 1, Name = "Proj1", Pipelines = [] },
            new() { Id = 2, Name = "Proj2", Pipelines = [] }
        };
        _repo.GetProjectsPagedAsync(null, null, false, 1, 25, null, ct: TestContext.Current.CancellationToken)
            .Returns((projects, 2));
        var gitDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        _repo.GetLastGitUpdatesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DateTime?> { [1] = gitDate });
        _repo.GetInternalRepositoryIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [1] = 41 });
        _repo.GetProjectListInsightsAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, ProjectListInsight>
            {
                [1] = new(
                    1, 81, "0123456789abcdef", "Ship portfolio view", gitDate,
                    91, "Deploy", PipelineStatus.Success, gitDate.AddMinutes(1),
                    AnalysisGrade.B, ProjectProductionStatus.Online, 7)
            });

        var result = await _sut.GetProjectsAsync(new ProjectPaginationRequest { Page = 1, PageSize = 25 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("Proj1", result.Items[0].Name);
        // The latest git activity is grafted onto the matching project; others stay null.
        Assert.Equal(gitDate, result.Items[0].LastGitUpdateAt);
        Assert.Equal(41, result.Items[0].InternalRepositoryId);
        Assert.Equal(81, result.Items[0].LastCommitId);
        Assert.Equal(91, result.Items[0].LastRunId);
        Assert.Equal(AnalysisGrade.B, result.Items[0].LatestGateGrade);
        Assert.Equal(ProjectProductionStatus.Online, result.Items[0].ProductionStatus);
        Assert.Equal(7, result.Items[0].OnlineUserCount);
        Assert.Null(result.Items[1].LastGitUpdateAt);
    }

    [Fact]
    public async Task GetProjectDetailAsync_Found_ReturnsDetailWithPipelines()
    {
        var project = new Project
        {
            Id = 1,
            Name = "My Project",
            Description = "Desc",
            RepositoryUrl = "https://github.com/test",
            DefaultBranch = "main",
            Status = ProjectStatus.Active,
            Tags = "[\"web\",\"api\"]",
            Pipelines =
            [
                new Pipeline
                {
                    Id = 10,
                    Name = "CI",
                    YamlDefinition = "yaml",
                    TriggerType = PipelineTriggerType.Manual,
                    Runs = [new PipelineRun { Id = 100, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow }]
                }
            ]
        };
        _repo.GetProjectDetailAsync(1, TestContext.Current.CancellationToken).Returns(project);
        _repo.GetLatestGateGradeAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.C, PipelineRunId = 2478 });

        var result = await _sut.GetProjectDetailAsync(1, ct: TestContext.Current.CancellationToken);

        // The detail reads the grade alone, not the list's whole insight.
        await _repo.DidNotReceiveWithAnyArgs().GetProjectListInsightsAsync(default!, default, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("My Project", result.Name);
        Assert.Single(result.Pipelines);
        Assert.Equal(PipelineStatus.Success, result.Pipelines[0].LastRunStatus);
        Assert.Equal(2, result.Tags.Count);
        Assert.Equal(AnalysisGrade.C, result.LatestGateGrade);
        Assert.Equal(2478, result.LatestGateGradeRunId);
    }

    [Fact]
    public async Task GetProjectDetailAsync_NotFound_ReturnsNull()
    {
        _repo.GetProjectDetailAsync(99, TestContext.Current.CancellationToken).Returns((Project?)null);

        var result = await _sut.GetProjectDetailAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetProjectDetailAsync_UsesActiveStepProjection()
    {
        var run = new PipelineRun
        {
            Id = 100,
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        var project = new Project
        {
            Id = 1,
            Name = "Project",
            Pipelines = [new Pipeline { Id = 10, Name = "CI", Runs = [run] }]
        };
        _repo.GetProjectDetailAsync(1, TestContext.Current.CancellationToken).Returns(project);
        _repo.GetActiveRunStepLabelsAsync(1, TestContext.Current.CancellationToken)
            .Returns(new Dictionary<int, string> { [100] = "Build · Tests" });

        var result = await _sut.GetProjectDetailAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Build · Tests", Assert.Single(Assert.Single(result!.Pipelines).RecentRuns).CurrentStep);
    }

    [Fact]
    public async Task CreateProjectAsync_AddsProjectAndReturnsDto()
    {
        _repo.AddProjectAsync(Arg.Any<Project>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask)
            .AndDoes(ci => ci.Arg<Project>().Id = 5);

        var result = await _sut.CreateProjectAsync(new CreateProjectRequest
        {
            Name = "New Proj",
            Description = "new",
            RepositoryUrl = "https://repo",
            DefaultBranch = "main",
            Tags = ["tag1"]
        }, creatorUserId: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Id);
        Assert.Equal("New Proj", result.Name);
        await _repo.Received(1).AddProjectAsync(Arg.Any<Project>(), TestContext.Current.CancellationToken);
        // No user row behind the caller: nobody is subscribed.
        await _userNotifications.DidNotReceive().SubscribeAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2034_CreateProjectAsync_SubscribesTheCreatorToTheNewProject()
    {
        _repo.AddProjectAsync(Arg.Any<Project>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask)
            .AndDoes(ci => ci.Arg<Project>().Id = 9);

        await _sut.CreateProjectAsync(new CreateProjectRequest { Name = "Followed" }, 42, TestContext.Current.CancellationToken);

        await _userNotifications.Received(1).SubscribeAsync(42, 9, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateProjectAsync_Found_UpdatesAndReturnsDto()
    {
        var project = new Project { Id = 1, Name = "Old", Pipelines = [] };
        _repo.FindProjectWithPipelinesAsync(1, TestContext.Current.CancellationToken).Returns(project);

        var result = await _sut.UpdateProjectAsync(1, new UpdateProjectRequest
        {
            Name = "Updated",
            Description = "Desc",
            Status = ProjectStatus.Archived,
            Tags = ["updated"]
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Updated", result.Name);
        Assert.Equal(ProjectStatus.Archived, result.Status);
        await _repo.Received(1).SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateProjectAsync_NotFound_ReturnsNull()
    {
        _repo.FindProjectWithPipelinesAsync(99, TestContext.Current.CancellationToken).Returns((Project?)null);

        var result = await _sut.UpdateProjectAsync(99, new UpdateProjectRequest { Name = "X" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetProjectDetailAsync_WithNoPipelineRuns_ReturnsNullLastRun()
    {
        var project = new Project
        {
            Id = 1,
            Name = "No Runs",
            Pipelines = [new Pipeline { Id = 10, Name = "CI", Runs = [] }]
        };
        _repo.GetProjectDetailAsync(1, TestContext.Current.CancellationToken).Returns(project);

        var result = await _sut.GetProjectDetailAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(result.Pipelines[0].LastRunStatus);
    }

    // --- GetProjectsWithRepoUrlAsync ---

    [Fact]
    public async Task GetProjectsWithRepoUrlAsync_ReturnsMappedList()
    {
        _repo.GetAllProjectsWithRepoUrlAsync(Arg.Any<CancellationToken>())
            .Returns([new Project { Id = 1, Name = "P1", RepositoryUrl = "https://github.com/test", Pipelines = [] }]);

        var result = await _sut.GetProjectsWithRepoUrlAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("https://github.com/test", result[0].RepositoryUrl);
    }

    // --- GetProjectServersAsync ---

    [Fact]
    public async Task GetProjectServersAsync_ReturnsMappedList()
    {
        _repo.GetProjectServersAsync(1, Arg.Any<CancellationToken>())
            .Returns([
                new ProjectServerRow
                {
                    Id = 1, ProjectId = 1, Type = ProjectServerType.AgentServer, ServerId = 5,
                    DisplayName = "web-01", Host = "web-01.local",
                    ServerName = "web-01", ServerHostname = "web-01.local", ServerStatus = ServerStatus.Online
                },
                new ProjectServerRow
                {
                    Id = 0, ProjectId = 1, Type = ProjectServerType.AgentServer, ServerId = 6,
                    DisplayName = "web-02", Host = "web-02.local", ViaEnvironmentId = 3, ViaEnvironmentName = "prod"
                }
            ]);

        var result = await _sut.GetProjectServersAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("web-01", result[0].DisplayName);
        Assert.Equal(ServerStatus.Online, result[0].ServerStatus);
        Assert.Null(result[0].ViaEnvironmentName);
        Assert.Equal(0, result[1].Id);
        Assert.Equal(3, result[1].ViaEnvironmentId);
        Assert.Equal("prod", result[1].ViaEnvironmentName);
    }

    // --- GetProjectTasksAsync ---

    [Fact]
    public async Task GetProjectTasksAsync_ReturnsPaginatedResult()
    {
        var tasks = new List<ServerTask>
        {
            new() { Id = 1, ServerId = 1, Name = "build", Command = "dotnet build", Server = new Server { Name = "build-01" } }
        };
        _repo.GetTasksPagedAsync(
                1, null, null, false, 1, 10, Arg.Any<CancellationToken>())
            .Returns((tasks, 1));

        var result = await _sut.GetProjectTasksAsync(1, new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("build", result.Items[0].Name);
        Assert.Equal("build-01", result.Items[0].ServerName);
    }

    // --- GetProjectLogsAsync ---

    [Fact]
    public async Task GetProjectLogsAsync_ReturnsPaginatedResult()
    {
        var logs = new List<TaskLog>
        {
            new() { Id = 1, TaskId = 1, Level = TaskLogLevel.Info, Message = "Done" }
        };
        _repo.GetLogsPagedAsync(
                1, null, null, false, 1, 10, Arg.Any<CancellationToken>())
            .Returns((logs, 1));

        var result = await _sut.GetProjectLogsAsync(1, new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("Done", result.Items[0].Message);
    }

    // --- GetProjectActivityAsync ---

    [Fact]
    public async Task GetProjectActivityAsync_CombinesTasksAndRuns()
    {
        _repo.GetRecentTasksAsync(1, 20, Arg.Any<CancellationToken>())
            .Returns([new ServerTask { Id = 1, Name = "deploy", Server = new Server { Name = "web-01" }, CreatedAt = DateTime.UtcNow }]);
        _repo.GetRecentPipelineRunsAsync(1, 20, Arg.Any<CancellationToken>())
            .Returns([new PipelineRun { Id = 1, Pipeline = new Pipeline { Name = "CI" }, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow }]);

        var result = await _sut.GetProjectActivityAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, a => a.Type == "task");
        Assert.Contains(result, a => a.Type == "pipeline");
    }
}
