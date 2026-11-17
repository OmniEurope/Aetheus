// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-18: a step turns Success together with the variables it published. A sibling step of the same stage
/// that completes a few milliseconds later advances the run on every step it reads as Success; when the
/// status was committed first and the variables saved afterwards, that advance dispatched the next stage
/// without them (the delivery contract of the nightly CI of 2026-11-15 got an empty candidate_prefix).
/// </summary>
public sealed class PipelineStepCompletionPublishesOutputsIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private const string Session = "agent-session";

    [Fact]
    public async Task AStepIsNeverReadAsSuccessfulBeforeItsPublishedVariablesAreStored()
    {
        await ResetAndMigrateAsync();
        var (stepRunId, taskId) = await SeedRunningStepAsync();

        // What a sibling step's advance would read, taken while the completion is in flight: the log lookup
        // that finds the step's output-variable lines is the first database work the completion does, and the
        // last moment before the outcome is stored by the code that had the race.
        var snapshots = new List<(TaskExecutionStatus Status, string? OutputVariablesJson)>();
        var logs = Substitute.For<ILogService>();
        logs.GetTaskOutputVariableLinesAsync(taskId, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await using var sibling = NewContext();
            var step = await sibling.PipelineStepRuns.AsNoTracking()
                .SingleAsync(item => item.Id == stepRunId, TestContext.Current.CancellationToken);
            snapshots.Add((step.Status, step.OutputVariablesJson));
            return new List<string>
            {
                "##aetheus[setvariable name=APPLICATION_CANDIDATE_VERSION_SEALED]c-0123456789abcdef0123456789abcdef01234567"
            };
        });

        await using var db = NewContext();
        var service = NewService(db, logs);
        Assert.True(await service.CompleteTaskAsync(
            taskId,
            new TaskResultDto
            {
                TaskId = taskId,
                Status = TaskExecutionStatus.Success,
                ExitCode = 0,
                AgentSessionId = Session,
                AgentSessionFencingToken = 1
            },
            TestContext.Current.CancellationToken));

        Assert.Single(snapshots);
        Assert.False(
            snapshots[0].Status == TaskExecutionStatus.Success && snapshots[0].OutputVariablesJson is null,
            "A sibling step read this step as Success while its published variables were not stored yet.");

        await using var verification = NewContext();
        var persisted = await verification.PipelineStepRuns.AsNoTracking()
            .SingleAsync(item => item.Id == stepRunId, TestContext.Current.CancellationToken);
        Assert.Equal(TaskExecutionStatus.Success, persisted.Status);
        Assert.Contains("c-0123456789abcdef0123456789abcdef01234567", persisted.OutputVariablesJson);
    }

    private static TaskService NewService(Aetheus.Back.Data.AppDbContext db, ILogService logs)
    {
        var pipelineHub = Substitute.For<IHubContext<PipelineHub>>();
        pipelineHub.Clients.Returns(Substitute.For<IHubClients>());
        var serverHub = Substitute.For<IHubContext<ServerHub>>();
        serverHub.Clients.Returns(Substitute.For<IHubClients>());
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var notifier = new TaskQueueNotifier(serverHub, encryption, NullLogger<TaskQueueNotifier>.Instance);
        return new TaskService(
            new TaskRepository(db, TimeProvider.System), logs, pipelineHub, serverHub,
            Substitute.For<IAuditService>(), TimeProvider.System, encryption, Substitute.For<IArtifactService>(),
            notifier, NullLogger<TaskService>.Instance, Substitute.For<IDomainEventDispatcher>());
    }

    private async Task<(int StepRunId, int TaskId)> SeedRunningStepAsync()
    {
        await using var seed = NewContext();
        var organization = new Organization { Name = $"org-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..12] };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        var project = new Project { Name = "Project", OrganizationId = organization.Id };
        var server = new Server
        {
            Name = "runner",
            Hostname = "127.0.0.1",
            OrganizationId = organization.Id,
            AgentSessionId = Session,
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
            StageName = "Seal inputs",
            StepName = "Seal the source commit and the payload inventory",
            Status = TaskExecutionStatus.Running
        };
        seed.PipelineStepRuns.Add(step);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        var task = new ServerTask
        {
            ServerId = server.Id,
            PipelineRunId = run.Id,
            PipelineStepRunId = step.Id,
            Name = "seal",
            Command = "true",
            Status = TaskExecutionStatus.Running,
            AssignedAgentSessionId = Session,
            AssignedAgentSessionFencingToken = 1
        };
        seed.Tasks.Add(task);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (step.Id, task.Id);
    }
}
