// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-12: after a cancellation the run's cleanup must not be dispatched while the agent is still killing the
/// process tree of the cancelled step. The agent tells the control plane that the task stopped with the
/// ordinary completion report (status Cancelled); until it does, or until the maximum wait elapses, the
/// cleanup stays Pending.
///
/// Real: the whole backend host, the real <see cref="IPipelineRunService"/> cancellation and scheduler, the
/// real <see cref="ITaskService"/> completion endpoint logic, and PostgreSQL. Simulated: the agent (the
/// test sends the completion report an agent would send) and the clock (the cancelled task's cancellation
/// instant is moved back in the database instead of sleeping for the maximum wait).
/// </summary>
public sealed class PipelineCancellationStopAcknowledgementIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string AgentSession = "0123456789abcdef0123456789abcdef";
    private const long FencingToken = 7;
    private const string CleanupStage = "System:Cleanup";

    private const string Yaml = """
        name: cancel-stop-ack
        trigger: manual
        stages:
          - name: build
            agent: default
            steps:
              - name: work
                shell: sleep 600
        """;

    [Fact]
    public async Task Cancel_HoldsTheCleanupBack_UntilTheAgentAcknowledgesTheStop()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (runId, workTaskId) = await SeedCancellableRunAsync(factory, workTaskStarted: true);

        await CancelAsync(factory, runId);

        // The step is cancelled in the database at once, but its process tree may still be dying on the
        // agent: nothing may be dispatched next to it.
        Assert.Equal(0, await CleanupTaskCountAsync(factory, runId));
        Assert.Equal(PipelineStatus.Running, await RunStatusAsync(factory, runId));

        Assert.True(await AcknowledgeStopAsync(factory, workTaskId));

        Assert.Equal(1, await CleanupTaskCountAsync(factory, runId));
    }

    [Fact]
    public async Task Cancel_DispatchesTheCleanupAfterTheMaximumWait_WhenTheAgentNeverAcknowledges()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (runId, workTaskId) = await SeedCancellableRunAsync(factory, workTaskStarted: true);

        await CancelAsync(factory, runId);
        Assert.Equal(0, await CleanupTaskCountAsync(factory, runId));

        // Still inside the wait: the periodic recovery pass leaves the cleanup alone.
        await ReconcileAsync(factory, runId);
        Assert.Equal(0, await CleanupTaskCountAsync(factory, runId));

        await MoveCancellationBackAsync(factory, workTaskId, PipelineRunScheduler.CancelledTaskStopMaxWait + TimeSpan.FromSeconds(30));
        await ReconcileAsync(factory, runId);

        Assert.Equal(1, await CleanupTaskCountAsync(factory, runId));
    }

    [Fact]
    public async Task AfterTheMaximumWait_TheWarningIsLoggedOnce_AndLaterAdvancesNeitherWaitNorWarnAgain()
    {
        var warnings = new ExpiredWaitWarningProvider();
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(
                services => services.AddSingleton<ILoggerProvider>(warnings)));
        var (runId, workTaskId) = await SeedCancellableRunAsync(factory, workTaskStarted: true);
        await CancelAsync(factory, runId);
        await MoveCancellationBackAsync(factory, workTaskId, PipelineRunScheduler.CancelledTaskStopMaxWait + TimeSpan.FromSeconds(30));

        await ReconcileAsync(factory, runId);
        Assert.Equal(1, warnings.Count);

        await ReconcileAsync(factory, runId);
        await ReconcileAsync(factory, runId);
        Assert.Equal(1, warnings.Count);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var task = await db.Tasks.AsNoTracking().SingleAsync(item => item.Id == workTaskId, TestContext.Current.CancellationToken);
        // The expiry is recorded as what it is: the agent never acknowledged.
        Assert.NotNull(task.StopWaitExpiredAt);
        Assert.Null(task.StopAcknowledgedAt);
        var repo = scope.ServiceProvider.GetRequiredService<IPipelineRepository>();
        var wait = await repo.GetCancelledTaskStopWaitAsync(runId, DateTime.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(new CancelledTaskStopWait(0, 0), wait);
    }

    private sealed class ExpiredWaitWarningProvider : ILoggerProvider
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(ExpiredWaitWarningProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning && formatter(state, exception).Contains("were not reported stopped within"))
                    Interlocked.Increment(ref owner._count);
            }
        }
    }

    [Fact]
    public async Task Cancel_KeepsTheCleanupBack_WhenTheReportComesFromAnAgentThatDoesNotHoldTheTask()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (runId, workTaskId) = await SeedCancellableRunAsync(factory, workTaskStarted: true);
        await CancelAsync(factory, runId);

        Assert.False(await AcknowledgeStopAsync(factory, workTaskId, fencingToken: FencingToken + 1));

        Assert.Equal(0, await CleanupTaskCountAsync(factory, runId));
    }

    [Fact]
    public async Task Cancel_DoesNotWait_ForATaskThatNeverStarted()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (runId, _) = await SeedCancellableRunAsync(factory, workTaskStarted: false);

        await CancelAsync(factory, runId);

        // Nothing was running on any agent, so there is no process tree to wait for.
        Assert.Equal(1, await CleanupTaskCountAsync(factory, runId));
    }

    private static async Task CancelAsync(WebApplicationFactory<Program> factory, int runId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
        Assert.True(await service.CancelRunAsync(
            runId, PipelineRunCancellation.ByUser("tester"), TestContext.Current.CancellationToken));
    }

    private static async Task ReconcileAsync(WebApplicationFactory<Program> factory, int runId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
        await service.ReconcileRunAsync(runId, TestContext.Current.CancellationToken);
    }

    /// <summary>The report the agent sends once the process tree of a server-cancelled task is gone.</summary>
    private static async Task<bool> AcknowledgeStopAsync(
        WebApplicationFactory<Program> factory, int taskId, long fencingToken = FencingToken)
    {
        using var scope = factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskService>();
        return await tasks.CompleteTaskAsync(taskId, new TaskResultDto
        {
            TaskId = taskId,
            Status = TaskExecutionStatus.Cancelled,
            ExitCode = -1,
            AgentSessionId = AgentSession,
            AgentSessionFencingToken = fencingToken
        }, TestContext.Current.CancellationToken);
    }

    private static async Task MoveCancellationBackAsync(
        WebApplicationFactory<Program> factory, int taskId, TimeSpan by)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cancelledAt = await db.Tasks.AsNoTracking().Where(task => task.Id == taskId)
            .Select(task => task.CompletedAt).SingleAsync(TestContext.Current.CancellationToken);
        var earlier = cancelledAt!.Value - by;
        await db.Tasks.Where(task => task.Id == taskId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.CompletedAt, earlier), TestContext.Current.CancellationToken);
    }

    private static async Task<int> CleanupTaskCountAsync(WebApplicationFactory<Program> factory, int runId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tasks.AsNoTracking().CountAsync(
            task => task.PipelineRunId == runId
                    && task.PipelineStepRun != null
                    && task.PipelineStepRun.StageName == CleanupStage,
            TestContext.Current.CancellationToken);
    }

    private static async Task<PipelineStatus> RunStatusAsync(WebApplicationFactory<Program> factory, int runId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PipelineRuns.AsNoTracking().Where(run => run.Id == runId)
            .Select(run => run.Status).SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A run as the engine leaves it mid-flight: prepared, one step Running on an online runner,
    /// the workspace cleanup still waiting.</summary>
    private static async Task<(int RunId, int WorkTaskId)> SeedCancellableRunAsync(
        WebApplicationFactory<Program> factory, bool workTaskStarted)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var organizationId = await db.Set<Organization>().OrderBy(o => o.Id).Select(o => o.Id).FirstAsync(ct);
        var now = DateTime.UtcNow;
        var server = new Server
        {
            Name = $"runner-{Guid.NewGuid():N}",
            Hostname = "test",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            LastHeartbeat = now,
            OrganizationId = organizationId,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\",\"shell.execute\"]",
            AgentSessionId = AgentSession,
            AgentSessionFencingToken = FencingToken,
            AgentSessionLeaseExpiresAt = now.AddHours(1)
        };
        db.Servers.Add(server);
        var pipeline = new Pipeline { Name = $"stop-ack-{Guid.NewGuid():N}", YamlDefinition = Yaml };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);

        var run = new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Running,
            StartedAt = now,
            AdditionalVariablesJson = "{}"
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var work = new PipelineStepRun
        {
            PipelineRunId = run.Id,
            StageName = "build",
            StepName = "work",
            Order = 1,
            ServerId = server.Id,
            Status = workTaskStarted ? TaskExecutionStatus.Running : TaskExecutionStatus.Assigned,
            StartedAt = workTaskStarted ? now : null
        };
        db.PipelineStepRuns.AddRange(
            new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = "System:Prepare",
                StepName = "prepare",
                Order = 0,
                ServerId = server.Id,
                Status = TaskExecutionStatus.Success,
                IsSystem = true
            },
            work,
            new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = CleanupStage,
                StepName = "Cleanup",
                Order = 2,
                Status = TaskExecutionStatus.Pending,
                IsSystem = true
            });
        await db.SaveChangesAsync(ct);

        var task = new ServerTask
        {
            ServerId = server.Id,
            PipelineRunId = run.Id,
            PipelineStepRunId = work.Id,
            Name = "work",
            Command = "sleep 600",
            Status = workTaskStarted ? TaskExecutionStatus.Running : TaskExecutionStatus.Assigned,
            AssignedAt = now,
            StartedAt = workTaskStarted ? now : null,
            AssignedAgentSessionId = AgentSession,
            AssignedAgentSessionFencingToken = FencingToken
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);
        return (run.Id, task.Id);
    }
}
