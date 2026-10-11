// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Apache;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Tasks;

/// <summary>
/// SITE-OFFLINE-001: a module service (here Apache) that queues through its own repository is refused
/// for an offline agent exactly like CreateTask/CreateOperation, with the same 409 contract, and
/// nothing is persisted, broadcast or audited. Wired to the real <see cref="TaskService"/> so the
/// refusal is the production check, not a substitute's.
/// </summary>
public sealed class ModuleTaskOfflineRefusalTests
{
    private readonly ITaskRepository _taskRepo = Substitute.For<ITaskRepository>();
    private readonly IApacheRepository _apacheRepo = Substitute.For<IApacheRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly ITaskQueueNotifier _notifier = Substitute.For<ITaskQueueNotifier>();
    private readonly ApacheService _sut;

    public ModuleTaskOfflineRefusalTests()
    {
        var taskService = new TaskService(
            _taskRepo, Substitute.For<ILogService>(), Substitute.For<IHubContext<PipelineHub>>(),
            Substitute.For<IHubContext<ServerHub>>(), _audit, TimeProvider.System,
            Substitute.For<IEncryptionService>(), Substitute.For<IArtifactService>(), _notifier,
            Substitute.For<ILogger<TaskService>>(), Substitute.For<IDomainEventDispatcher>());
        _sut = new ApacheService(_apacheRepo, _audit, taskService);
    }

    [Theory]
    [InlineData(ServerStatus.Offline)]
    [InlineData(ServerStatus.Disabled)]
    public async Task ApacheTest_OnAnAgentThatIsNotOnline_IsRefusedWithConflict_AndQueuesNothing(ServerStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        _taskRepo.GetServerStatusAsync(4, Arg.Any<CancellationToken>()).Returns(status);

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.ExecuteActionAsync(4, new ApacheActionRequest { Action = ApacheAction.TestConfig }, ct));

        Assert.Contains("agent is offline", error.Message, StringComparison.Ordinal);
        await _apacheRepo.DidNotReceiveWithAnyArgs().AddTaskAsync(default!, ct);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyTaskQueuedAsync(default!, default, ct);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default!, ct);
    }

    [Fact]
    public async Task ApacheTest_OnAnOnlineAgent_IsQueuedAndBroadcast()
    {
        var ct = TestContext.Current.CancellationToken;
        _taskRepo.GetServerStatusAsync(4, Arg.Any<CancellationToken>()).Returns(ServerStatus.Online);

        await _sut.ExecuteActionAsync(4, new ApacheActionRequest { Action = ApacheAction.TestConfig }, ct);

        await _apacheRepo.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(task => task.ServerId == 4 && task.Operation == OperationKind.ApacheTestConfig), ct);
        await _notifier.Received(1).NotifyTaskQueuedAsync(
            Arg.Is<ServerTask>(task => task.ServerId == 4), Arg.Any<string?>(), ct);
    }
}
