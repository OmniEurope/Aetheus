// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using NSubstitute;

namespace Aetheus.Back.Tests.Tasks;

public sealed class TaskQueuePersistenceTests
{
    [Fact]
    public async Task PersistAndNotifyAsync_PersistsBeforeBroadcasting()
    {
        var calls = new List<string>();
        var taskService = Substitute.For<ITaskService>();
        var task = new ServerTask();
        var ct = TestContext.Current.CancellationToken;
        taskService.When(service => service.NotifyTaskQueuedAsync(task, ct: ct))
            .Do(_ => calls.Add("notify"));

        await TaskQueuePersistence.PersistAndNotifyAsync(
            (_, receivedToken) =>
            {
                Assert.Equal(ct, receivedToken);
                calls.Add("persist");
                return Task.CompletedTask;
            },
            taskService,
            task,
            ct);

        Assert.Equal(["persist", "notify"], calls);
    }

    [Fact]
    public async Task PersistAndNotifyAsync_PersistenceFailure_DoesNotBroadcast()
    {
        var taskService = Substitute.For<ITaskService>();
        var task = new ServerTask();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TaskQueuePersistence.PersistAndNotifyAsync(
                (_, _) => Task.FromException(new InvalidOperationException("persist failed")),
                taskService,
                task,
                TestContext.Current.CancellationToken));

        Assert.Equal("persist failed", error.Message);
        await taskService.DidNotReceiveWithAnyArgs().NotifyTaskQueuedAsync(
            default!, ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PersistAndNotifyAsync_OfflineRefusal_PersistsAndBroadcastsNothing()
    {
        // SITE-OFFLINE-001: the online check runs first, so a refused task never reaches the repository.
        var taskService = Substitute.For<ITaskService>();
        var task = new ServerTask { ServerId = 7 };
        var ct = TestContext.Current.CancellationToken;
        taskService.EnsureServerOnlineAsync(7, ct).Returns(Task.FromException(new ConflictException("offline")));
        var persisted = false;

        await Assert.ThrowsAsync<ConflictException>(() =>
            TaskQueuePersistence.PersistAndNotifyAsync(
                (_, _) => { persisted = true; return Task.CompletedTask; }, taskService, task, ct));

        Assert.False(persisted);
        await taskService.DidNotReceiveWithAnyArgs().NotifyTaskQueuedAsync(default!, ct: ct);
    }
}
