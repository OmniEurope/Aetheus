// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class TaskServiceOperationTests
{
    private readonly ITaskRepository _repo = Substitute.For<ITaskRepository>();
    private readonly TaskService _sut;

    public TaskServiceOperationTests()
    {
        var pipelineHub = Substitute.For<IHubContext<PipelineHub>>();
        var serverHub = Substitute.For<IHubContext<ServerHub>>();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        pipelineHub.Clients.Returns(clients);
        serverHub.Clients.Returns(clients);
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var taskQueueNotifier = new TaskQueueNotifier(
            serverHub,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<TaskQueueNotifier>>());

        // No orchestration port any more: the run advances by domain event, so the spy is the
        // dispatcher.
        _sut = new TaskService(_repo, Substitute.For<ILogService>(),
            pipelineHub, serverHub, Substitute.For<IAuditService>(), TimeProvider.System, encryption, Substitute.For<IArtifactService>(),
            taskQueueNotifier,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<TaskService>>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>());
    }

    [Fact]
    public async Task CreateOperationAsync_BuildsTypedOperationTaskAndReturnsDto()
    {
        // AddTaskAsync echoes the persisted task back (assigns Id in prod); return it so MapToDto works.
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>()).Returns(ci => ci.Arg<ServerTask>());

        var result = await _sut.CreateOperationAsync(new CreateOperationRequest
        {
            ServerId = 5,
            Name = "Restart nginx",
            Target = "nginx",
            Operation = OperationKind.ServiceRestart,
            TimeoutSeconds = 30
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Restart nginx", result.Name);
        Assert.Equal(ExecutorType.Operation, result.Executor);
        await _repo.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.ServerId == 5 && t.Operation == OperationKind.ServiceRestart && t.Command == "nginx"),
            Arg.Any<CancellationToken>());
    }
}
