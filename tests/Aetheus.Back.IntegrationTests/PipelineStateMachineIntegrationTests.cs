// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PipelineStateMachineIntegrationTests(PostgresFixture fixture)
{
    private const string SystemPrepare = "System:Prepare";
    private const string RunnerCapabilities = "[\"pipeline.build\",\"shell.execute\"]";
    private const string TwoStageYaml = """
        name: two-stage
        trigger: manual
        stages:
          - name: build
            agent: default
            steps:
              - name: compile
                shell: echo build
          - name: deploy
            agent: default
            depends_on: [build]
            steps:
              - name: ship
                shell: echo deploy
        """;

    private const string SingleStageYaml = """
        name: single
        trigger: manual
        stages:
          - name: build
            agent: default
            steps:
              - name: compile
                shell: echo build
        """;

    private const string RetryYaml = """
        name: retry-test
        trigger: manual
        stages:
          - name: build
            agent: default
            steps:
              - name: flaky
                shell: echo maybe
                retry_count: 2
        """;

    private const string ContinueOnErrorYaml = """
        name: continue-test
        trigger: manual
        stages:
          - name: build
            agent: default
            steps:
              - name: optional
                shell: echo maybe
                continue_on_error: true
          - name: deploy
            agent: default
            depends_on: [build]
            steps:
              - name: ship
                shell: echo deploy
        """;

    private const string ConditionYaml = """
        name: conditional
        trigger: manual
        stages:
          - name: build
            agent: default
            steps:
              - name: compile
                shell: echo build
          - name: deploy-prod
            agent: default
            depends_on: [build]
            condition: "eq(variables['ENV'], 'production')"
            steps:
              - name: ship
                shell: echo deploy
        """;

    [Fact]
    public async Task FullCycle_SingleStage_PendingToSuccess()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);

        int runId;
        int stepId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pipeline = new Pipeline { Name = $"sm-single-{Guid.NewGuid():N}", YamlDefinition = SingleStageYaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var run = NewRunningRun(pipeline.Id);
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.PipelineStepRuns.AddRange(
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = SystemPrepare,
                    StepName = "prepare",
                    Order = 0,
                    Status = TaskExecutionStatus.Success,
                    IsSystem = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "build",
                    StepName = "compile",
                    Order = 1,
                    Status = TaskExecutionStatus.Success
                });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            runId = run.Id;
            stepId = await db.PipelineStepRuns
                .Where(s => s.PipelineRunId == run.Id && s.StageName == "build")
                .Select(s => s.Id)
                .FirstAsync(cancellationToken: TestContext.Current.CancellationToken);

            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            await service.AdvanceStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(PipelineStatus.Success, run.Status);
            Assert.NotNull(run.CompletedAt);
        }
    }

    [Fact]
    public async Task MultiStage_BuildSuccess_DeployGetsDispatched()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);

        int runId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orgId = await db.Set<Organization>().Select(o => o.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            var server = new Server
            {
                Name = $"runner-{Guid.NewGuid():N}",
                Hostname = "test",
                Status = ServerStatus.Online,
                PipelineRunnerEnabled = true,
                LastHeartbeat = DateTime.UtcNow,
                OrganizationId = orgId,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilitiesJson = RunnerCapabilities
            };
            db.Servers.Add(server);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var pipeline = new Pipeline { Name = $"sm-multi-{Guid.NewGuid():N}", YamlDefinition = TwoStageYaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var run = NewRunningRun(pipeline.Id);
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.PipelineStepRuns.AddRange(
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = SystemPrepare,
                    StepName = "prepare",
                    Order = 0,
                    Status = TaskExecutionStatus.Success,
                    IsSystem = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "build",
                    StepName = "compile",
                    Order = 1,
                    Status = TaskExecutionStatus.Success
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "deploy",
                    StepName = "ship",
                    Order = 2,
                    Status = TaskExecutionStatus.Pending
                });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            runId = run.Id;

            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            await service.AdvanceStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var run = await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken: TestContext.Current.CancellationToken);
            var deployStep = await db.PipelineStepRuns.AsNoTracking()
                .FirstAsync(s => s.PipelineRunId == runId && s.StageName == "deploy", cancellationToken: TestContext.Current.CancellationToken);

            // The background dispatcher can complete the tiny `ship` task before this read. Both
            // observable states prove the contract: continue_on_error did not stop the run, and the
            // dependent stage was assigned. A completed run must additionally prove that stage green.
            Assert.Contains(run.Status, new[] { PipelineStatus.Running, PipelineStatus.Success });
            Assert.True(deployStep.ServerId.HasValue);
            if (run.Status == PipelineStatus.Success)
                Assert.Equal(TaskExecutionStatus.Success, deployStep.Status);

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.PipelineRunId == runId && t.Name == "ship", cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(task);
        }
    }

    [Fact]
    public async Task FailedStep_NonContinuable_RunFailsImmediately()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);

        int runId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pipeline = new Pipeline { Name = $"sm-fail-{Guid.NewGuid():N}", YamlDefinition = TwoStageYaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var run = NewRunningRun(pipeline.Id);
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.PipelineStepRuns.AddRange(
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = SystemPrepare,
                    StepName = "prepare",
                    Order = 0,
                    Status = TaskExecutionStatus.Success,
                    IsSystem = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "build",
                    StepName = "compile",
                    Order = 1,
                    Status = TaskExecutionStatus.Failed,
                    ContinueOnError = false
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "deploy",
                    StepName = "ship",
                    Order = 2,
                    Status = TaskExecutionStatus.Pending
                });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            runId = run.Id;

            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            await service.AdvanceStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(PipelineStatus.Failed, run.Status);
            Assert.NotNull(run.CompletedAt);
        }
    }

    [Fact]
    public async Task FailedStepWithRetry_DecrementsAndResets()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);

        int runId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var orgId = await db.Set<Organization>().Select(o => o.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            var server = new Server
            {
                Name = $"runner-{Guid.NewGuid():N}",
                Hostname = "test",
                Status = ServerStatus.Online,
                PipelineRunnerEnabled = true,
                LastHeartbeat = DateTime.UtcNow,
                OrganizationId = orgId,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilitiesJson = RunnerCapabilities
            };
            db.Servers.Add(server);

            var pipeline = new Pipeline { Name = $"sm-retry-{Guid.NewGuid():N}", YamlDefinition = RetryYaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var run = NewRunningRun(pipeline.Id);
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.PipelineStepRuns.AddRange(
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = SystemPrepare,
                    StepName = "prepare",
                    Order = 0,
                    Status = TaskExecutionStatus.Success,
                    IsSystem = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "build",
                    StepName = "flaky",
                    Order = 1,
                    Status = TaskExecutionStatus.Failed,
                    RetryCount = 2,
                    ExitCode = 1,
                    StartedAt = DateTime.UtcNow.AddSeconds(-5),
                    CompletedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            runId = run.Id;

            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            await service.AdvanceStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var step = await db.PipelineStepRuns.AsNoTracking()
                .FirstAsync(s => s.PipelineRunId == runId && s.StepName == "flaky", cancellationToken: TestContext.Current.CancellationToken);
            // The retry resets the failed step (RetryCount 2->1, ExitCode/timestamps cleared) and re-dispatches it.
            // Because this run has an online runner provisioned, the reset step is immediately re-assigned to it
            // (Pending -> Assigned, see PipelineRunHelpers.MarkStepDispatched) rather than left waiting for pickup.
            Assert.Equal(TaskExecutionStatus.Assigned, step.Status);
            Assert.Equal(1, step.RetryCount);
            Assert.Null(step.ExitCode);
            Assert.Null(step.StartedAt);
            Assert.Null(step.CompletedAt);

            var run = await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(PipelineStatus.Running, run.Status);
        }
    }

    [Fact]
    public async Task ContinueOnError_FailedOptional_AdvancesToNextStage()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);

        int runId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orgId = await db.Set<Organization>().Select(o => o.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            var server = new Server
            {
                Name = $"runner-{Guid.NewGuid():N}",
                Hostname = "test",
                Status = ServerStatus.Online,
                PipelineRunnerEnabled = true,
                LastHeartbeat = DateTime.UtcNow,
                OrganizationId = orgId,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilitiesJson = RunnerCapabilities
            };
            db.Servers.Add(server);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var pipeline = new Pipeline { Name = $"sm-continue-{Guid.NewGuid():N}", YamlDefinition = ContinueOnErrorYaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var run = NewRunningRun(pipeline.Id);
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.PipelineStepRuns.AddRange(
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = SystemPrepare,
                    StepName = "prepare",
                    Order = 0,
                    Status = TaskExecutionStatus.Success,
                    IsSystem = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "build",
                    StepName = "optional",
                    Order = 1,
                    Status = TaskExecutionStatus.Failed,
                    ContinueOnError = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "deploy",
                    StepName = "ship",
                    Order = 2,
                    Status = TaskExecutionStatus.Pending
                });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            runId = run.Id;

            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            await service.AdvanceStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var run = await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(PipelineStatus.Running, run.Status);

            var deployStep = await db.PipelineStepRuns.AsNoTracking()
                .FirstAsync(s => s.PipelineRunId == runId && s.StageName == "deploy", cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(deployStep.ServerId.HasValue);
        }
    }

    [Fact]
    public async Task CancelRun_RunningPipeline_WaitsForRunningWorkBeforeBecomingCancelled()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);

        int runId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pipeline = new Pipeline { Name = $"sm-cancel-{Guid.NewGuid():N}", YamlDefinition = TwoStageYaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var startedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
            var run = new PipelineRun
            {
                PipelineId = pipeline.Id,
                Status = PipelineStatus.Running,
                StartedAt = startedAt
            };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.PipelineStepRuns.AddRange(
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "build",
                    StepName = "compile",
                    Order = 0,
                    Status = TaskExecutionStatus.Running,
                    StartedAt = startedAt
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "deploy",
                    StepName = "ship",
                    Order = 1,
                    Status = TaskExecutionStatus.Pending
                });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            runId = run.Id;

            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            var result = await service.CancelRunAsync(run.Id, PipelineRunCancellation.ByUser("alice"), ct: TestContext.Current.CancellationToken);
            Assert.True(result);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var run = await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(PipelineStatus.Running, run.Status);
            var variables = PipelineRunHelpers.DeserializeResolvedVariables(run.AdditionalVariablesJson);
            Assert.Equal("true", variables[PipelineRunService.CancellationRequestedVariable]);

            var pendingSteps = await db.PipelineStepRuns.AsNoTracking()
                .Where(s => s.PipelineRunId == runId && s.Status == TaskExecutionStatus.Pending)
                .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(pendingSteps);

            var runningStep = await db.PipelineStepRuns
                .SingleAsync(
                    step => step.PipelineRunId == runId && step.Status == TaskExecutionStatus.Running,
                    cancellationToken: TestContext.Current.CancellationToken);
            runningStep.Status = TaskExecutionStatus.Success;
            runningStep.CompletedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            await service.AdvanceStageAsync(runId, "build", ct: TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.PipelineRuns.AsNoTracking().FirstAsync(
                candidate => candidate.Id == runId,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(PipelineStatus.Cancelled, run.Status);
        }
    }

    [Fact]
    public async Task ConditionFalse_StageSkipped_RunCompletesSuccess()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);

        int runId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pipeline = new Pipeline { Name = $"sm-cond-{Guid.NewGuid():N}", YamlDefinition = ConditionYaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var run = new PipelineRun
            {
                PipelineId = pipeline.Id,
                Status = PipelineStatus.Running,
                StartedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
                ResolvedVariablesJson = """{"ENV":"staging"}"""
            };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.PipelineStepRuns.AddRange(
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = SystemPrepare,
                    StepName = "prepare",
                    Order = 0,
                    Status = TaskExecutionStatus.Success,
                    IsSystem = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "build",
                    StepName = "compile",
                    Order = 1,
                    Status = TaskExecutionStatus.Success
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "deploy-prod",
                    StepName = "ship",
                    Order = 2,
                    Status = TaskExecutionStatus.Pending
                });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            runId = run.Id;

            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            await service.AdvanceStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var deployStep = await db.PipelineStepRuns.AsNoTracking()
                .FirstAsync(s => s.PipelineRunId == runId && s.StageName == "deploy-prod", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(TaskExecutionStatus.Cancelled, deployStep.Status);

            var run = await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(PipelineStatus.Success, run.Status);
        }
    }

    [Fact]
    public async Task OutputVariables_EnableConditionAndInjectIntoSubsequentStage()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);

        int runId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var yaml = """
                name: output-vars
                trigger: manual
                stages:
                  - name: build
                    agent: default
                    steps:
                      - name: produce
                        shell: echo "##aetheus[setvariable name=VERSION]1.2.3"
                  - name: deploy
                    agent: default
                    depends_on: [build]
                    condition: eq(variables['PROMOTE'], 'true')
                    steps:
                      - name: consume
                        shell: echo $(VERSION)
                """;

            var orgId = await db.Set<Organization>().Select(o => o.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            var server = new Server
            {
                Name = $"runner-ov-{Guid.NewGuid():N}",
                Hostname = "test",
                Status = ServerStatus.Online,
                PipelineRunnerEnabled = true,
                LastHeartbeat = DateTime.UtcNow,
                OrganizationId = orgId,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilitiesJson = RunnerCapabilities
            };
            db.Servers.Add(server);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var pipeline = new Pipeline { Name = $"sm-output-{Guid.NewGuid():N}", YamlDefinition = yaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var run = NewRunningRun(pipeline.Id);
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.PipelineStepRuns.AddRange(
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = SystemPrepare,
                    StepName = "prepare",
                    Order = 0,
                    Status = TaskExecutionStatus.Success,
                    IsSystem = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "build",
                    StepName = "produce",
                    Order = 1,
                    Status = TaskExecutionStatus.Success,
                    OutputVariablesJson = """{"VERSION":"1.2.3","PROMOTE":"true"}"""
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "deploy",
                    StepName = "consume",
                    Order = 2,
                    Status = TaskExecutionStatus.Pending
                });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            runId = run.Id;

            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            await service.AdvanceStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.PipelineRunId == runId && t.Name == "consume", cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(task);
            Assert.Contains("1.2.3", task.Command);
        }
    }

    private static PipelineRun NewRunningRun(int pipelineId) => new()
    {
        PipelineId = pipelineId,
        Status = PipelineStatus.Running,
        StartedAt = TimeProvider.System.GetUtcNow().UtcDateTime
    };
}
