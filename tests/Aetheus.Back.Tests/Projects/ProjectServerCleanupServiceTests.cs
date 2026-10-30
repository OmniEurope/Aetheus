// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Projects;

/// <summary> Recette R-97 (ADR-058): the agent tasks of a project deletion. The removal list comes only from a
/// discovery the agent of the same server printed for this project, and a deletion counts as clean only when
/// every resource found is reported removed. </summary>
public sealed class ProjectServerCleanupServiceTests
{
    private const string Capable = "{\"software\":[\"project.resources\"]}";

    private static readonly CleanupServerInfo Prod = new(3, "vps-prod", 1, ServerStatus.Online, Capable, false);
    private static readonly CleanupServerInfo Offline = new(4, "vps-qa", 1, ServerStatus.Offline, Capable, false);
    private static readonly ProjectDeletionScope Scope = new(7, "Portfolio", 1, [], [], [], [], [], [], [], [], []);

    private static readonly ProjectServerResourceRef Web = new(ProjectServerResourceKind.Container, "portfolio-prod-web-1", "portfolio-prod");
    private static readonly ProjectServerResourceRef Data = new(ProjectServerResourceKind.Volume, "portfolio-prod_db", "portfolio-prod");
    private static readonly ProjectServerResourceRef OtherProject = new(ProjectServerResourceKind.Container, "other-prod-web-1", "other-prod");

    private readonly IProjectServerCleanupRepository _repo = Substitute.For<IProjectServerCleanupRepository>();
    private readonly IProjectDeletionRepository _deletion = Substitute.For<IProjectDeletionRepository>();
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IProjectServerCleanupGuardSource _guards = Substitute.For<IProjectServerCleanupGuardSource>();
    private ProjectServerCleanupGuards _currentGuards = new(ServerWriteAccess.All, new HashSet<string>());
    private readonly List<ServerTask> _stored = [];
    private readonly Dictionary<int, string> _outputs = [];
    private readonly List<ServerTask> _queued = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<int, DateTime> _lastActivity = [];
    private readonly ProjectServerCleanupService _sut;

    public ProjectServerCleanupServiceTests()
    {
        SetInputs(new ProjectServerCleanupInputs(7, "Portfolio", 1,
            [
                new CleanupLibrary("portfolio.prod", null, null, new Dictionary<string, string>
                {
                    ["COMPOSE_PROJECT"] = "portfolio-prod",
                    ["APP_HOST"] = "app.portfolio.example",
                }),
                new CleanupLibrary("portfolio.qa", null, null, new Dictionary<string, string> { ["COMPOSE_PROJECT"] = "portfolio-qa" }),
            ],
            [new CleanupEnvironment(21, "prod", [Prod]), new CleanupEnvironment(22, "qa", [Offline])],
            [], [], []));
        _repo.GetProjectTasksAsync(7, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => _stored.Where(task => call.Arg<IReadOnlyCollection<int>>().Contains(task.Id)).ToList());
        _repo.GetTaskOutputsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<int, string>)call.Arg<IReadOnlyCollection<int>>()
                .ToDictionary(id => id, id => _outputs.GetValueOrDefault(id, string.Empty)));
        _repo.AddTaskAsync(Arg.Do<ServerTask>(_queued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _deletion.ResolveScopeAsync(7, Arg.Any<CancellationToken>()).Returns(Scope);
        _repo.GetLastProjectActivityAsync(7, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<int, DateTime>)_lastActivity);
        _guards.GetAsync(Arg.Any<CancellationToken>()).Returns(_ => _currentGuards);
        _sut = new ProjectServerCleanupService(_repo, _deletion, _tasks, _guards, _audit, _time);
    }

    private DateTime MinutesAgo(int minutes) => _time.GetUtcNow().UtcDateTime.AddMinutes(-minutes);

    private void Stored(int id, int serverId, OperationKind operation, TaskExecutionStatus status, params string[] lines) =>
        StoredAt(MinutesAgo(1), id, serverId, operation, status, lines);

    private void StoredAt(DateTime createdAt, int id, int serverId, OperationKind operation, TaskExecutionStatus status, params string[] lines)
    {
        _stored.Add(new ServerTask { Id = id, ServerId = serverId, Operation = operation, Status = status, Command = "7", CreatedAt = createdAt });
        _outputs[id] = string.Join('\n', lines);
    }

    private void Discovery(int id, params ProjectServerResourceRef[] found) => DiscoveryAt(MinutesAgo(2), id, found);

    private void DiscoveryAt(DateTime createdAt, int id, params ProjectServerResourceRef[] found) =>
        StoredAt(createdAt, id, 3, OperationKind.ProjectResourcesDiscover, TaskExecutionStatus.Success,
            [.. found.Select(item => ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Found, item)), ProjectServerResourceOutput.CompleteLine]);

    // The plan and the task list read the project through two repository methods (the full one and the light one the
    // polled task list uses); both answer the same project here, so a test sees one project whichever is asked.
    private void SetInputs(ProjectServerCleanupInputs inputs)
    {
        _repo.LoadInputsAsync(7, Arg.Any<CancellationToken>()).Returns(inputs);
        _repo.LoadTaskInputsAsync(7, Arg.Any<CancellationToken>()).Returns(inputs);
    }

    private void ProdOnly() =>
        SetInputs(new ProjectServerCleanupInputs(7, "Portfolio", 1,
            [new CleanupLibrary("portfolio.prod", null, null, new Dictionary<string, string> { ["COMPOSE_PROJECT"] = "portfolio-prod" })],
            [new CleanupEnvironment(21, "prod", [Prod])], [], [], []));

    private static ProjectServerResourceSpec SpecOf(ServerTask task)
    {
        var variables = JsonSerializer.Deserialize<Dictionary<string, string>>(task.EnvironmentVariables)!;
        var spec = ProjectServerResourceSpec.Decode(variables[ProjectServerResourceSpec.EnvironmentVariable], out var refusal);
        Assert.Null(refusal);
        return spec!;
    }

    [Fact]
    public async Task QueueDiscovery_AsksEachReadyServer_WithATypedOperationWhoseTargetIsTheProjectId()
    {
        var queued = await _sut.QueueDiscoveryAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal(3, Assert.Single(queued).ServerId);
        var task = Assert.Single(_queued);
        Assert.Equal((OperationKind.ProjectResourcesDiscover, "7", 3), (task.Operation, task.Command, task.ServerId));
        var spec = SpecOf(task);
        Assert.Equal(["portfolio-prod"], spec.ComposeProjects);
        Assert.Equal(["app.portfolio.example"], spec.Hosts);
        Assert.Empty(spec.Remove);
        await _tasks.Received(1).EnsureServerOnlineAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueRemoval_RemovesExactlyWhatTheDiscoveryFound_WithinTheProjectIdentity()
    {
        Discovery(41, Web, Data, OtherProject);

        var queued = await _sut.QueueRemovalAsync(7, new ProjectServerCleanupRequest { DiscoveryTaskIds = [41], AcknowledgeVolumeDataLoss = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectServerTaskPhase.Removal, Assert.Single(queued).Phase);
        var task = Assert.Single(_queued);
        Assert.Equal((OperationKind.ProjectResourcesRemove, "7"), (task.Operation, task.Command));
        Assert.Equal([Web, Data], SpecOf(task).Remove);
        await _audit.Received(1).LogAsync("ServerResourcesRemovalQueued", "Project", 7, "vps-prod: 2 resource(s)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutWriteOnTheServer_NothingIsReadNorRemoved_AndTheVerdictSaysWhy()
    {
        // A discovery the agent printed (queued earlier, for instance by an administrator) is no right to remove.
        Discovery(41, Web);
        _currentGuards = new ProjectServerCleanupGuards(ServerWriteAccess.None, new HashSet<string>());

        Assert.Empty(await _sut.QueueDiscoveryAsync(7, TestContext.Current.CancellationToken));
        Assert.Empty(await _sut.QueueRemovalAsync(7, new ProjectServerCleanupRequest { DiscoveryTaskIds = [41] }, TestContext.Current.CancellationToken));
        Assert.Empty(_queued);
        var verdict = await _sut.VerifyAsync(7, [41], TestContext.Current.CancellationToken);
        Assert.NotNull(verdict);
        Assert.False(verdict.Clean);
        Assert.Contains("vps-prod: not removed, no write access on this server", verdict.Problems);
        Assert.DoesNotContain(verdict.Summary, "server resources removed: 1", StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithWriteOnTheServer_TheRemovalIsQueuedThere()
    {
        Discovery(41, Web);
        _currentGuards = new ProjectServerCleanupGuards(new ServerWriteAccess(new HashSet<int> { Prod.Id }), new HashSet<string>());

        Assert.Single(await _sut.QueueRemovalAsync(7, new ProjectServerCleanupRequest { DiscoveryTaskIds = [41] }, TestContext.Current.CancellationToken));
        Assert.Equal(Prod.Id, Assert.Single(_queued).ServerId);
    }

    [Fact]
    public async Task QueueRemoval_OfVolumes_WithoutTheirDataLossAcknowledged_IsRefused()
    {
        Discovery(41, Web, Data);

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => _sut.QueueRemovalAsync(7,
            new ProjectServerCleanupRequest { DiscoveryTaskIds = [41] }, TestContext.Current.CancellationToken));

        Assert.Contains("volumes", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_queued);
    }

    [Fact]
    public async Task QueueRemoval_WithATaskOfAnotherProjectOrServer_IsRefused()
    {
        Discovery(41, Web);
        // A discovery on a server of another organization, so outside the plan.
        Stored(43, 9, OperationKind.ProjectResourcesDiscover, TaskExecutionStatus.Success,
            ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Found, Web), ProjectServerResourceOutput.CompleteLine);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.QueueRemovalAsync(7,
            new ProjectServerCleanupRequest { DiscoveryTaskIds = [41, 99] }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.QueueRemovalAsync(7,
            new ProjectServerCleanupRequest { DiscoveryTaskIds = [43] }, TestContext.Current.CancellationToken));
        Assert.Empty(_queued);
        Assert.Empty(await _sut.GetTasksAsync(7, [43], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueRemoval_WhileARunOfTheProjectIsActive_IsRefused()
    {
        Discovery(41, Web);
        _deletion.CountActiveRunsAsync(Scope, Arg.Any<CancellationToken>()).Returns(1);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.QueueRemovalAsync(7,
            new ProjectServerCleanupRequest { DiscoveryTaskIds = [41] }, TestContext.Current.CancellationToken));
        Assert.Empty(_queued);
    }

    [Fact]
    public async Task Verify_APartialFailure_IsReportedPerResource_AndIsNotClean()
    {
        Discovery(41, Web, Data);
        Stored(42, 3, OperationKind.ProjectResourcesRemove, TaskExecutionStatus.Failed,
            ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Removed, Web),
            ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Failed, Data, "volume is in use"),
            ProjectServerResourceOutput.CompleteLine);

        var verdict = await _sut.VerifyAsync(7, [41, 42], TestContext.Current.CancellationToken);
        var tasks = await _sut.GetTasksAsync(7, [41, 42], TestContext.Current.CancellationToken);

        Assert.NotNull(verdict);
        Assert.False(verdict.Clean);
        Assert.Contains("vps-prod: Volume portfolio-prod_db not removed", verdict.Problems);
        Assert.Contains("vps-qa: not asked (Offline)", verdict.Problems);
        Assert.StartsWith("server resources removed: 1; servers: 2;", verdict.Summary, StringComparison.Ordinal);
        var removal = Assert.Single(tasks, task => task.Phase == ProjectServerTaskPhase.Removal);
        Assert.Equal(ProjectServerTaskState.Failed, removal.State);
        Assert.Equal(
            [(ProjectServerResourceOutcome.Removed, "prod"), (ProjectServerResourceOutcome.Failed, "prod")],
            removal.Resources.Select(item => (item.Outcome, item.Environment!)));
    }

    [Fact]
    public async Task GetTasks_ReturnsEachTasksServerNamePhaseStateAndResourcesWithTheirEnvironment()
    {
        Discovery(41, Web, Data);
        Stored(42, 3, OperationKind.ProjectResourcesRemove, TaskExecutionStatus.Running);

        var tasks = await _sut.GetTasksAsync(7, [41, 42], TestContext.Current.CancellationToken);

        Assert.Equal(
            [(41, 3, "vps-prod", ProjectServerTaskPhase.Discovery, ProjectServerTaskState.Succeeded),
             (42, 3, "vps-prod", ProjectServerTaskPhase.Removal, ProjectServerTaskState.Running)],
            tasks.Select(item => (item.TaskId, item.ServerId, item.ServerName, item.Phase, item.State)));
        Assert.Equal(
            [(ProjectServerResourceKind.Container, "portfolio-prod-web-1", "prod", ProjectServerResourceOutcome.Found),
             (ProjectServerResourceKind.Volume, "portfolio-prod_db", "prod", ProjectServerResourceOutcome.Found)],
            tasks[0].Resources.Select(item => (item.Kind, item.Name, item.Environment!, item.Outcome)));
        Assert.Empty(tasks[1].Resources);
    }

    [Fact]
    public async Task GetTasks_ReadsTheOutputsOfAllTheTasksInOneGroupedQuery_AndTheLightInputsOnly()
    {
        Discovery(41, Web);
        Discovery(43, Data);
        Stored(42, 3, OperationKind.ProjectResourcesRemove, TaskExecutionStatus.Running);
        _repo.ClearReceivedCalls();

        var tasks = await _sut.GetTasksAsync(7, [41, 42, 43], TestContext.Current.CancellationToken);

        Assert.Equal(3, tasks.Count);
        await _repo.Received(1).GetTaskOutputsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.OrderBy(id => id).SequenceEqual(new[] { 41, 42, 43 })), Arg.Any<CancellationToken>());
        await _repo.Received(1).LoadTaskInputsAsync(7, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().LoadInputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().GetLastProjectActivityAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTasks_ForAProjectThatDoesNotExist_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetTasksAsync(99, [41], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Verify_EveryResourceRemoved_OnEveryServer_IsClean()
    {
        ProdOnly();
        Discovery(41, Web, Data);
        Stored(42, 3, OperationKind.ProjectResourcesRemove, TaskExecutionStatus.Success,
            ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Removed, Web),
            ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Absent, Data),
            ProjectServerResourceOutput.CompleteLine);

        var verdict = await _sut.VerifyAsync(7, [41, 42], TestContext.Current.CancellationToken);

        Assert.NotNull(verdict);
        Assert.True(verdict.Clean, string.Join("; ", verdict.Problems));
        Assert.Equal("server resources removed: 2; servers: 1; problems: 0", verdict.Summary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Verify_AnUnprovedRemoval_IsNeverClean(bool removalRan, bool leftSomething)
    {
        ProdOnly();
        Discovery(41, Web);
        if (removalRan)
            Stored(42, 3, OperationKind.ProjectResourcesRemove, leftSomething ? TaskExecutionStatus.Failed : TaskExecutionStatus.Timeout,
                leftSomething
                    ? [ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Removed, Web),
                       ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.NotListed, Data), ProjectServerResourceOutput.CompleteLine]
                    // The agent stopped before its closing line: what it printed is not the whole story.
                    : [ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Removed, Web)]);

        var verdict = await _sut.VerifyAsync(7, removalRan ? [41, 42] : [41], TestContext.Current.CancellationToken);

        Assert.NotNull(verdict);
        Assert.False(verdict.Clean);
    }

    [Fact]
    public async Task Verify_WithoutAnyTask_SaysTheServerWasNotRead()
    {
        var verdict = await _sut.VerifyAsync(7, [], TestContext.Current.CancellationToken);

        Assert.NotNull(verdict);
        Assert.Contains("vps-prod: its resources were not read", verdict.Problems);
    }

    [Theory]
    [InlineData(59, true)]
    [InlineData(61, false)]
    public async Task Verify_AnEmptyDiscovery_ProvesTheServerCleanOnlyWithinTheValidityWindow(int minutesAgo, bool clean)
    {
        ProdOnly();
        DiscoveryAt(MinutesAgo(minutesAgo), 41);

        var verdict = await _sut.VerifyAsync(7, [41], TestContext.Current.CancellationToken);

        Assert.NotNull(verdict);
        Assert.Equal(clean, verdict.Clean);
        if (!clean) Assert.Contains("vps-prod: its resources were not read recently (read again before deleting)", verdict.Problems);
    }

    [Fact]
    public async Task Verify_ADiscoveryReadBeforeTheProjectsLastRunOnTheServer_ProvesNothing()
    {
        ProdOnly();
        DiscoveryAt(MinutesAgo(10), 41);
        // A deployment ran on the server after that read: what it created was never listed.
        _lastActivity[3] = MinutesAgo(5);

        var verdict = await _sut.VerifyAsync(7, [41], TestContext.Current.CancellationToken);

        Assert.NotNull(verdict);
        Assert.False(verdict.Clean);
        Assert.Contains("vps-prod: its resources were not read recently (read again before deleting)", verdict.Problems);
    }

    [Fact]
    public async Task QueueRemoval_FromADiscoveryOlderThanTheValidityWindow_RemovesNothing()
    {
        DiscoveryAt(MinutesAgo(120), 41, Web);

        var queued = await _sut.QueueRemovalAsync(7, new ProjectServerCleanupRequest { DiscoveryTaskIds = [41] },
            TestContext.Current.CancellationToken);

        Assert.Empty(queued);
        Assert.Empty(_queued);
    }

    [Fact]
    public async Task AVirtualHostOrCertificateTheAgentCannotRemove_IsNotAsked_AndKeepsTheVerdictUnclean()
    {
        // The default plan's server declares project.resources only: no apache.manage, no certbot.manage.
        var site = new ProjectServerResourceRef(ProjectServerResourceKind.VirtualHost, "app.portfolio.example-ssl.conf", "app.portfolio.example");
        var certificate = new ProjectServerResourceRef(ProjectServerResourceKind.Certificate, "app.portfolio.example", "app.portfolio.example");
        Discovery(41, Web, site, certificate);

        await _sut.QueueRemovalAsync(7, new ProjectServerCleanupRequest { DiscoveryTaskIds = [41] }, TestContext.Current.CancellationToken);
        Stored(42, 3, OperationKind.ProjectResourcesRemove, TaskExecutionStatus.Success,
            ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Removed, Web), ProjectServerResourceOutput.CompleteLine);
        var verdict = await _sut.VerifyAsync(7, [41, 42], TestContext.Current.CancellationToken);

        Assert.Equal([Web], SpecOf(Assert.Single(_queued)).Remove);
        Assert.NotNull(verdict);
        Assert.False(verdict.Clean);
        Assert.Contains("vps-prod: VirtualHost app.portfolio.example-ssl.conf cannot be removed (the agent lacks apache.manage, certbot.manage)", verdict.Problems);
        Assert.Contains("vps-prod: Certificate app.portfolio.example cannot be removed (the agent lacks apache.manage, certbot.manage)", verdict.Problems);
    }

    [Fact]
    public async Task R51_ARefusedValue_IsAuditedAsLeftInPlace_ButNeverKeepsTheProjectWaiting_AndAnEnvironmentWithoutServerIsSilent()
    {
        SetInputs(new ProjectServerCleanupInputs(7, "Portfolio", 1,
            [
                new CleanupLibrary("portfolio.prod", null, null, new Dictionary<string, string>
                {
                    ["COMPOSE_PROJECT"] = "portfolio-prod",
                    ["STATE_DIR"] = "/etc",
                }),
                new CleanupLibrary("portfolio.qa", null, null, new Dictionary<string, string> { ["COMPOSE_PROJECT"] = "portfolio-qa" }),
            ],
            [new CleanupEnvironment(21, "prod", [Prod]), new CleanupEnvironment(22, "qa", [])],
            [], [], []));
        Discovery(41);

        var verdict = await _sut.VerifyAsync(7, [41], TestContext.Current.CancellationToken);

        Assert.NotNull(verdict);
        Assert.True(verdict.Clean, string.Join("; ", verdict.Problems));
        Assert.Contains("values not used, left in place: prod: STATE_DIR=/etc not used (InvalidName)", verdict.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("portfolio-qa", verdict.Summary, StringComparison.Ordinal);
    }
}
