// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>A user cancellation must reach the task an agent is executing: the agent only learns of it
/// through the server-side task status (it stops the process tree when its tracked task turns terminal),
/// so a Running task of a cancelled stage has to become Cancelled together with its step run.</summary>
public sealed class PipelineRunningStepCancellationIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task UserCancellation_CancelsRunningTaskAndStep_ButNotThePreservedStage_OnPostgres()
    {
        await ResetAndMigrateAsync();
        int runId;
        int workTaskId;
        int teardownTaskId;
        await using (var seed = NewContext())
        {
            var organization = new Organization
            {
                Name = $"org-{Guid.NewGuid():N}",
                Slug = $"s{Guid.NewGuid():N}"[..12]
            };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = new Project { Name = "Project", OrganizationId = organization.Id };
            var server = new Server
            {
                Name = "runner",
                Hostname = "127.0.0.1",
                OrganizationId = organization.Id,
                AgentSessionId = "agent-session",
                AgentSessionFencingToken = 1,
                AgentSessionLeaseExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };
            seed.Projects.Add(project);
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var pipeline = new Pipeline
            {
                Name = "pipeline",
                ProjectId = project.Id,
                YamlDefinition = "name: pipeline\nstages: []"
            };
            seed.Pipelines.Add(pipeline);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var run = new PipelineRun
            {
                PipelineId = pipeline.Id,
                Status = PipelineStatus.Running,
                AdditionalVariablesJson = "{}"
            };
            seed.PipelineRuns.Add(run);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var workStep = new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = "Build",
                StepName = "sleep",
                Status = TaskExecutionStatus.Running
            };
            var teardownStep = new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = "Teardown",
                StepName = "release",
                Status = TaskExecutionStatus.Running
            };
            seed.PipelineStepRuns.AddRange(workStep, teardownStep);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var workTask = NewRunningTask(server.Id, run.Id, workStep.Id, "sleep");
            var teardownTask = NewRunningTask(server.Id, run.Id, teardownStep.Id, "release");
            seed.Tasks.AddRange(workTask, teardownTask);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            runId = run.Id;
            workTaskId = workTask.Id;
            teardownTaskId = teardownTask.Id;
        }

        await using (var cancellationDb = NewContext())
        {
            var repository = new PipelineRepository(
                cancellationDb,
                TimeProvider.System,
                NullLogger<PipelineRepository>.Instance,
                new PipelineTaskLifecycleRepository(cancellationDb, TimeProvider.System),
                new PipelineRunLineageRepository(cancellationDb));
            await repository.CancelPendingStepRunsExceptStagesAsync(
                runId, ["Teardown"], TestContext.Current.CancellationToken);
        }

        await using var verification = NewContext();
        var work = await verification.Tasks.AsNoTracking()
            .SingleAsync(item => item.Id == workTaskId, TestContext.Current.CancellationToken);
        var workStepAfter = await verification.PipelineStepRuns.AsNoTracking()
            .SingleAsync(item => item.Id == work.PipelineStepRunId, TestContext.Current.CancellationToken);
        var teardown = await verification.Tasks.AsNoTracking()
            .SingleAsync(item => item.Id == teardownTaskId, TestContext.Current.CancellationToken);
        var teardownStepAfter = await verification.PipelineStepRuns.AsNoTracking()
            .SingleAsync(item => item.Id == teardown.PipelineStepRunId, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Cancelled, work.Status);
        Assert.NotNull(work.CompletedAt);
        Assert.Equal(TaskExecutionStatus.Cancelled, workStepAfter.Status);
        Assert.NotNull(workStepAfter.CompletedAt);
        Assert.Equal(TaskExecutionStatus.Running, teardown.Status);
        Assert.Equal(TaskExecutionStatus.Running, teardownStepAfter.Status);
    }

    private static ServerTask NewRunningTask(int serverId, int runId, int stepRunId, string name) => new()
    {
        ServerId = serverId,
        PipelineRunId = runId,
        PipelineStepRunId = stepRunId,
        Name = name,
        Command = "sleep 75",
        Status = TaskExecutionStatus.Running,
        AssignedAgentSessionId = "agent-session",
        AssignedAgentSessionFencingToken = 1
    };
}
