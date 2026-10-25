// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PipelineCancellationIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Theory]
    [InlineData(TaskExecutionStatus.Pending)]
    [InlineData(TaskExecutionStatus.Assigned)]
    [InlineData(TaskExecutionStatus.Running)]
    public async Task CancellationAndTaskStart_ConvergeToOneCoherentStateOnPostgres(
        TaskExecutionStatus initialStatus)
    {
        await ResetAndMigrateAsync();
        int runId;
        int taskId;
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
            var step = new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = "work",
                StepName = "build",
                Status = initialStatus
            };
            seed.PipelineStepRuns.Add(step);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var serverTask = new ServerTask
            {
                ServerId = server.Id,
                PipelineRunId = run.Id,
                PipelineStepRunId = step.Id,
                Name = "build",
                Command = "dotnet build",
                Status = initialStatus,
                AssignedAgentSessionId = "agent-session",
                AssignedAgentSessionFencingToken = 1
            };
            seed.Tasks.Add(serverTask);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            runId = run.Id;
            taskId = serverTask.Id;
        }

        await using var cancellationDb = NewContext();
        await using var startDb = NewContext();
        var pipelineRepository = new PipelineRepository(
            cancellationDb,
            TimeProvider.System,
            NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(cancellationDb, TimeProvider.System),
            new PipelineRunLineageRepository(cancellationDb));
        var taskRepository = new TaskRepository(startDb, TimeProvider.System);
        var task = await startDb.Tasks.SingleAsync(
            item => item.Id == taskId, TestContext.Current.CancellationToken);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = Task.Run(async () =>
        {
            await gate.Task;
            if (initialStatus == TaskExecutionStatus.Running)
            {
                await pipelineRepository.CancelActiveStepRunsAndTasksAsync(
                    runId, TestContext.Current.CancellationToken);
            }
            else
            {
                await pipelineRepository.CancelPendingStepRunsExceptStagesAsync(
                    runId, [], TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);
        var start = Task.Run(async () =>
        {
            await gate.Task;
            if (initialStatus == TaskExecutionStatus.Running)
            {
                return await taskRepository.TryCompleteTaskWithLeaseAsync(
                    task,
                    "agent-session",
                    1,
                    TaskExecutionStatus.Success,
                    0,
                    DateTime.SpecifyKind(new DateTime(2026, 7, 28, 12, 1, 0), DateTimeKind.Utc),
                    "{}",
                    TestContext.Current.CancellationToken);
            }
            return await taskRepository.TryStartTaskAsync(
                task,
                DateTime.SpecifyKind(new DateTime(2026, 7, 28, 12, 0, 0), DateTimeKind.Utc),
                TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        gate.SetResult();

        await Task.WhenAll(cancel, start);
        var startResult = await start;

        await using var verification = NewContext();
        var persistedTask = await verification.Tasks.AsNoTracking()
            .SingleAsync(item => item.Id == taskId, TestContext.Current.CancellationToken);
        var persistedStep = await verification.PipelineStepRuns.AsNoTracking()
            .SingleAsync(item => item.Id == persistedTask.PipelineStepRunId,
                TestContext.Current.CancellationToken);
        Assert.Equal(persistedTask.Status, persistedStep.Status);
        if (initialStatus == TaskExecutionStatus.Running)
        {
            Assert.True(
                persistedTask.Status is TaskExecutionStatus.Success or TaskExecutionStatus.Cancelled,
                $"Unexpected task status {persistedTask.Status}.");
            Assert.Equal(persistedTask.Status == TaskExecutionStatus.Success, startResult);
        }
        else
        {
            Assert.True(
                persistedTask.Status is TaskExecutionStatus.Running or TaskExecutionStatus.Cancelled,
                $"Unexpected task status {persistedTask.Status}.");
            Assert.Equal(persistedTask.Status == TaskExecutionStatus.Running, startResult);
            var persistedRun = await verification.PipelineRuns.AsNoTracking()
                .SingleAsync(item => item.Id == runId, TestContext.Current.CancellationToken);
            var variables = PipelineRunHelpers.DeserializeResolvedVariables(
                persistedRun.AdditionalVariablesJson);
            Assert.Equal("true", variables[PipelineRunHelpers.CancellationRequestedVariable]);
        }
    }
}
