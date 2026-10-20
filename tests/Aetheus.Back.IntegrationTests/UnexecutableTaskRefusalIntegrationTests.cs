// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Production incident of 2026-10-19: a <c>type: aetheus</c> step queued a function task for a server
/// whose agent (1.3.15) did not declare <c>pipeline.functions</c>. The claim skipped it silently, the
/// task sat Pending for as long as the agent kept polling, and nothing said why. These tests run the
/// coordinator's sweep against PostgreSQL: the task now fails with an actionable reason, and work
/// fenced behind an agent update still resumes once the update is confirmed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UnexecutableTaskRefusalIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const string OldSession = "session-old";
    private const string TargetVersion = "1.3.16";

    private static readonly string[] OutdatedCapabilities =
        [AgentCapabilities.PipelineBuild, AgentCapabilities.SelfUpdate, AgentCapabilities.ShellExecution];

    [Fact]
    public async Task PendingFunctionTask_OnAnAgentWithoutTheCapability_IsFailedWithAnActionableReason()
    {
        await ResetAndMigrateAsync();
        var seeded = await SeedAsync();

        // The incident as it was: the agent polls, and the claim silently hands it nothing.
        await using (var db = NewContext())
        {
            var claimed = await new TaskRepository(db, TimeProvider.System).ClaimPendingTasksAsync(
                seeded.ServerId, 10, OldSession, TestContext.Current.CancellationToken);
            Assert.Empty(claimed);
        }

        var harness = new Harness(this);
        await harness.Coordinator.CoordinateAsync(TestContext.Current.CancellationToken);

        await using var verify = NewContext();
        var task = await verify.Tasks.Include(item => item.Logs)
            .SingleAsync(item => item.Id == seeded.TaskId, TestContext.Current.CancellationToken);
        Assert.Equal(TaskExecutionStatus.Failed, task.Status);
        Assert.Equal(TaskFailureCodes.AgentIncompatible, task.FailureCode);
        Assert.Equal(-1, task.ExitCode);
        Assert.NotNull(task.CompletedAt);
        Assert.Contains("1.3.15", task.FailureReason);
        Assert.Contains(TargetVersion, task.FailureReason);
        Assert.Contains(AgentCapabilities.PipelineFunctions, task.FailureReason);
        Assert.Contains("Mettre à jour", task.FailureReason);
        Assert.Contains(task.Logs, log => log.Level == TaskLogLevel.Error
            && log.Message.StartsWith("[incident:AgentIncompatible]", StringComparison.Ordinal));

        var step = await verify.PipelineStepRuns
            .SingleAsync(item => item.Id == seeded.StepId, TestContext.Current.CancellationToken);
        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        Assert.Equal(TaskFailureCodes.AgentIncompatible, step.FailureCode);
        Assert.Equal(task.FailureReason, step.FailureReason);
        Assert.NotNull(step.CompletedAt);

        // The run is told, so it fails instead of waiting on a step that can never start.
        await harness.Events.Received(1).DispatchStrictAsync(
            Arg.Is<PipelineStepTaskCompletedEvent>(e => e.PipelineRunId == seeded.RunId
                && e.StageName == "work" && e.TaskStatus == TaskExecutionStatus.Failed),
            Arg.Any<CancellationToken>());
        await harness.Audit.Received(1).LogAsync(
            "TaskRefusedAgentIncompatible", "ServerTask", seeded.TaskId,
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PendingTask_BehindAnAgentUpdate_IsKeptAndResumesOnceTheUpdateIsConfirmed()
    {
        await ResetAndMigrateAsync();
        var seeded = await SeedAsync();
        var harness = new Harness(this);

        // An operator starts the update. The self-update is exclusive and goes first; the function
        // task stays fenced behind it, and the sweep must not blame an agent that is being replaced.
        await using (var db = NewContext())
        {
            var repository = new AgentUpdateRepository(db, TimeProvider.System);
            var server = await db.Servers.SingleAsync(TestContext.Current.CancellationToken);
            var reserved = await repository.ReserveAsync(
                server, harness.Release, "admin", TestContext.Current.CancellationToken);
            var queued = await repository.TryQueueAsync(
                reserved.Request.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(queued.CreatedTask);

            var claimed = await new TaskRepository(db, TimeProvider.System).ClaimPendingTasksAsync(
                seeded.ServerId, 10, OldSession, TestContext.Current.CancellationToken);
            Assert.Equal(OperationKind.AgentSelfUpdate, Assert.Single(claimed).Operation);

            await repository.MarkProgressAsync(
                seeded.ServerId,
                new AgentUpdateProgressDto
                {
                    ServerId = seeded.ServerId,
                    Phase = AgentUpdatePhase.LaunchingUpdater,
                    Percent = 90
                },
                TestContext.Current.CancellationToken);
        }

        await harness.Coordinator.CoordinateAsync(TestContext.Current.CancellationToken);
        await AssertTaskStatusAsync(seeded.TaskId, TaskExecutionStatus.Pending);

        // The new agent session confirms the target release; its heartbeat publishes the capabilities.
        string[] updatedCapabilities = [.. OutdatedCapabilities, AgentCapabilities.PipelineFunctions];
        await using (var db = NewContext())
        {
            var confirmed = await new AgentUpdateRepository(db, TimeProvider.System).ConfirmFromHeartbeatAsync(
                seeded.ServerId,
                new ServerHeartbeatDto
                {
                    AgentSessionId = "session-new",
                    AgentVersion = TargetVersion,
                    AgentProtocolVersion = AgentProtocol.CurrentVersion,
                    AgentCapabilities = [.. updatedCapabilities]
                },
                TestContext.Current.CancellationToken);
            Assert.Equal(AgentUpdateRequestStatus.Confirmed, confirmed!.Status);

            await db.Servers.Where(server => server.Id == seeded.ServerId).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(server => server.AgentVersion, TargetVersion)
                    .SetProperty(server => server.AgentSessionId, "session-new")
                    .SetProperty(server => server.AgentSessionLeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1))
                    .SetProperty(server => server.AgentCapabilitiesJson, SerializeCapabilities(updatedCapabilities)),
                TestContext.Current.CancellationToken);
        }

        await using (var db = NewContext())
        {
            var resumed = await new TaskRepository(db, TimeProvider.System).ClaimPendingTasksAsync(
                seeded.ServerId, 10, "session-new", TestContext.Current.CancellationToken);
            var task = Assert.Single(resumed);
            Assert.Equal(seeded.TaskId, task.Id);
            Assert.Equal(TaskExecutionStatus.Assigned, task.Status);
        }
    }

    [Fact]
    public async Task PendingTask_YoungerThanTheGrace_OrOnAnAgentThatIsNotPolling_IsLeftAlone()
    {
        await ResetAndMigrateAsync();
        var young = await SeedAsync(taskAge: TimeSpan.FromSeconds(10));
        await using (var db = NewContext())
        {
            await db.Servers.Where(server => server.Id == young.ServerId).ExecuteUpdateAsync(
                setters => setters.SetProperty(server => server.AgentSessionLeaseExpiresAt, DateTime.UtcNow.AddMinutes(5)),
                TestContext.Current.CancellationToken);
        }

        var harness = new Harness(this);
        await harness.Coordinator.CoordinateAsync(TestContext.Current.CancellationToken);
        await AssertTaskStatusAsync(young.TaskId, TaskExecutionStatus.Pending);

        // Old enough, but the agent stopped polling: the offline handling owns this case.
        await using (var db = NewContext())
        {
            await db.Tasks.Where(task => task.Id == young.TaskId).ExecuteUpdateAsync(
                setters => setters.SetProperty(task => task.CreatedAt, DateTime.UtcNow.AddMinutes(-10)),
                TestContext.Current.CancellationToken);
            await db.Servers.Where(server => server.Id == young.ServerId).ExecuteUpdateAsync(
                setters => setters.SetProperty(server => server.AgentSessionLeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1)),
                TestContext.Current.CancellationToken);
        }
        await harness.Coordinator.CoordinateAsync(TestContext.Current.CancellationToken);
        await AssertTaskStatusAsync(young.TaskId, TaskExecutionStatus.Pending);
        await harness.Events.DidNotReceiveWithAnyArgs().DispatchStrictAsync(
            default(PipelineStepTaskCompletedEvent)!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TaskClaimedByTheAgentBeforeTheSweepWrites_IsNotOverwritten()
    {
        await ResetAndMigrateAsync();
        var seeded = await SeedAsync();
        await using (var db = NewContext())
        {
            await db.Tasks.Where(task => task.Id == seeded.TaskId).ExecuteUpdateAsync(
                setters => setters.SetProperty(task => task.Status, TaskExecutionStatus.Assigned),
                TestContext.Current.CancellationToken);
        }

        await using var refuseDb = NewContext();
        var refused = await new TaskRepository(refuseDb, TimeProvider.System).TryFailPendingTaskAsync(
            seeded.TaskId, TaskFailureCodes.AgentIncompatible, "reason", DateTime.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.False(refused);
        await AssertTaskStatusAsync(seeded.TaskId, TaskExecutionStatus.Assigned);
    }

    private async Task AssertTaskStatusAsync(int taskId, TaskExecutionStatus expected)
    {
        await using var db = NewContext();
        var status = await db.Tasks.Where(task => task.Id == taskId).Select(task => task.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, status);
    }

    private static string SerializeCapabilities(IEnumerable<string> capabilities) =>
        System.Text.Json.JsonSerializer.Serialize(capabilities.Order(StringComparer.Ordinal));

    private sealed record Seeded(int ServerId, int RunId, int StepId, int TaskId);

    /// <summary>An online, polling server running agent 1.3.15 that does not declare
    /// <c>pipeline.functions</c>, and a function task queued for it by a pipeline step.</summary>
    private async Task<Seeded> SeedAsync(TimeSpan? taskAge = null)
    {
        await using var seed = NewContext();
        var organization = new Organization { Name = $"org-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..12] };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        var project = new Project { Name = "Project", OrganizationId = organization.Id };
        var server = new Server
        {
            Name = "vps",
            Hostname = "vps.example",
            OrganizationId = organization.Id,
            Status = ServerStatus.Online,
            AgentVersion = "1.3.15",
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = SerializeCapabilities(OutdatedCapabilities),
            AgentSessionId = OldSession,
            AgentSessionFencingToken = 1,
            AgentSessionLeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            LastHeartbeat = DateTime.UtcNow
        };
        seed.Projects.Add(project);
        seed.Servers.Add(server);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        var pipeline = new Pipeline { Name = "aetheus-release-fast", ProjectId = project.Id, YamlDefinition = "name: p\nstages: []" };
        seed.Pipelines.Add(pipeline);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, AdditionalVariablesJson = "{}" };
        seed.PipelineRuns.Add(run);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        var step = new PipelineStepRun
        {
            PipelineRunId = run.Id,
            StageName = "work",
            StepName = "bump",
            Status = TaskExecutionStatus.Assigned,
            ServerId = server.Id
        };
        seed.PipelineStepRuns.Add(step);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        var task = new ServerTask
        {
            ServerId = server.Id,
            PipelineRunId = run.Id,
            PipelineStepRunId = step.Id,
            Name = "bump",
            Command = "bump-version",
            Operation = OperationKind.PipelineFunction,
            Status = TaskExecutionStatus.Pending
        };
        seed.Tasks.Add(task);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        // The context stamps CreatedAt on insert, so the age is applied afterwards.
        var createdAt = DateTime.UtcNow - (taskAge ?? TimeSpan.FromMinutes(15));
        await seed.Tasks.Where(item => item.Id == task.Id).ExecuteUpdateAsync(
            setters => setters.SetProperty(item => item.CreatedAt, createdAt),
            TestContext.Current.CancellationToken);
        return new Seeded(server.Id, run.Id, step.Id, task.Id);
    }

    /// <summary>The coordinator wired to real repositories on the test database; only the outward
    /// edges (hubs, audit, notifications, domain events, release catalog) are substitutes.</summary>
    private sealed class Harness
    {
        public Harness(UnexecutableTaskRefusalIntegrationTests test)
        {
            Release = new AgentReleaseManifestDto
            {
                SoftwareVersion = TargetVersion,
                ProtocolVersion = AgentProtocol.CurrentVersion,
                MinimumSupportedProtocol = AgentProtocol.MinimumSupportedVersion,
                MaximumSupportedProtocol = AgentProtocol.MaximumSupportedVersion,
                SoftwareCapabilities = [.. AgentCapabilities.SoftwareCapabilities.Order(StringComparer.Ordinal)
                    .Append(AgentCapabilities.PipelineFunctions).Order(StringComparer.Ordinal)]
            };
            var catalog = Substitute.For<IAgentReleaseCatalog>();
            catalog.Current.Returns(Release);
            var options = test.BuildOptions();
            Events = Substitute.For<IDomainEventDispatcher>();
            Audit = Substitute.For<IAuditService>();
            var services = new ServiceCollection()
                .AddSingleton(TimeProvider.System)
                .AddSingleton(catalog)
                .AddSingleton(Events)
                .AddSingleton(Audit)
                .AddSingleton(Substitute.For<IHubContext<PipelineHub>>())
                .AddSingleton(Substitute.For<IHubContext<ServerHub>>())
                .AddSingleton(Substitute.For<ITaskService>())
                .AddSingleton(new AgentUpdateNotificationPublisher(
                    Substitute.For<IUserNotificationService>(),
                    NullLogger<AgentUpdateNotificationPublisher>.Instance))
                .AddLogging()
                .AddScoped(_ => new AppDbContext(options))
                .AddScoped<IAgentUpdateRepository, AgentUpdateRepository>()
                .AddScoped<ITaskRepository, TaskRepository>()
                .AddScoped<IUnexecutableTaskRefuser, UnexecutableTaskRefuser>()
                .BuildServiceProvider();
            Coordinator = new AgentUpdateCoordinatorService(
                services.GetRequiredService<IServiceScopeFactory>(),
                services.GetRequiredService<IHubContext<ServerHub>>(),
                TimeProvider.System,
                NullLogger<AgentUpdateCoordinatorService>.Instance);
        }

        public AgentReleaseManifestDto Release { get; }
        public IDomainEventDispatcher Events { get; }
        public IAuditService Audit { get; }
        public AgentUpdateCoordinatorService Coordinator { get; }
    }
}
