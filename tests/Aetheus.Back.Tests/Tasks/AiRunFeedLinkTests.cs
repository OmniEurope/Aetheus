// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R-12: the live task feed (the queued event and the active list the front reads on arrival) tells the AI task
/// list which definition an AI run in progress belongs to, so the list matches the run to its row by id and a rename of
/// the definition never loses it. Only an AI run created from a definition carries an id.
/// </summary>
public sealed class AiRunFeedLinkTests
{
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly ITaskRepository _repo = Substitute.For<ITaskRepository>();
    private readonly IHubContext<ServerHub> _serverHub = Substitute.For<IHubContext<ServerHub>>();
    private readonly IClientProxy _proxy = Substitute.For<IClientProxy>();
    private readonly TaskQueueNotifier _notifier;
    private readonly TaskService _sut;

    public AiRunFeedLinkTests()
    {
        // The stored environment is the plaintext JSON here: the protection itself is covered by TaskEnvProtection tests.
        _encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(_proxy);
        _serverHub.Clients.Returns(clients);
        var pipelineHub = Substitute.For<IHubContext<PipelineHub>>();
        pipelineHub.Clients.Returns(clients);
        _notifier = new TaskQueueNotifier(_serverHub, _encryption, Substitute.For<ILogger<TaskQueueNotifier>>());
        _sut = new TaskService(_repo, Substitute.For<ILogService>(), pipelineHub, _serverHub, Substitute.For<IAuditService>(),
            TimeProvider.System, _encryption, Substitute.For<IArtifactService>(), _notifier,
            Substitute.For<ILogger<TaskService>>(), Substitute.For<IDomainEventDispatcher>());
    }

    private static ServerTask AiRun(int id, string name, string environment) => new()
    {
        Id = id,
        ServerId = 5,
        Name = name,
        Operation = OperationKind.AiRun,
        Status = TaskExecutionStatus.Running,
        EnvironmentVariables = environment
    };

    private static string DefinitionEnvironment(int definitionId) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["AETHEUS_AI_TASK_DEFINITION_ID"] = definitionId.ToString() });

    private async Task<IReadOnlyList<ServerTaskDto>> ActiveAsync(params ServerTask[] tasks)
    {
        _repo.GetTasksByStatusesPagedAsync(null, 1, 200, Arg.Any<List<TaskExecutionStatus>>(), null, Arg.Any<CancellationToken>())
            .Returns((tasks.ToList(), tasks.Length));
        return await _sut.GetActiveTasksAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheActiveList_CarriesTheOperationAndTheDefinitionOfAnAiRun()
    {
        var active = await ActiveAsync(AiRun(1, "nightly review", DefinitionEnvironment(7)));

        var run = Assert.Single(active);
        Assert.Equal(OperationKind.AiRun, run.Operation);
        Assert.Equal(7, run.AiTaskDefinitionId);
    }

    [Fact]
    public async Task TheActiveList_GivesNoDefinitionToATaskThatIsNotAnAiRunOrWhoseEnvironmentCannotBeRead()
    {
        var shell = new ServerTask { Id = 2, ServerId = 5, Name = "Apache restart", Status = TaskExecutionStatus.Running, EnvironmentVariables = DefinitionEnvironment(7) };
        var pipelineStep = AiRun(3, "AI review step", "{}");
        var corrupt = AiRun(4, "broken", "{not json");

        var active = await ActiveAsync(shell, pipelineStep, corrupt);

        Assert.All(active, task => Assert.Null(task.AiTaskDefinitionId));
        Assert.Equal(OperationKind.None, active.Single(task => task.Id == 2).Operation);
    }

    [Fact]
    public async Task TheQueuedEvent_CarriesTheDefinitionOfAnAiRun()
    {
        await _notifier.NotifyTaskQueuedAsync(AiRun(9, "weekly audit", DefinitionEnvironment(8)), ct: TestContext.Current.CancellationToken);

        await _proxy.Received().SendCoreAsync("TaskQueued",
            Arg.Is<object?[]>(args => args.Length == 1 && args[0] is ServerTaskDto
                && ((ServerTaskDto)args[0]!).AiTaskDefinitionId == 8 && ((ServerTaskDto)args[0]!).Operation == OperationKind.AiRun),
            Arg.Any<CancellationToken>());
    }
}
