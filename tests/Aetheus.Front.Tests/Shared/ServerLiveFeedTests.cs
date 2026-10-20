// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Unit tests feed live events through hub-handler entry points because the test transport
/// cannot reach a hub.</summary>
public sealed class ServerLiveFeedTests : BunitContext
{
    private readonly HubConnectionFactory _hubFactory;

    public ServerLiveFeedTests()
    {
        BunitTestHelper.RegisterServices(this);
        _hubFactory = Services.GetRequiredService<HubConnectionFactory>();
    }

    private static Func<int, CancellationToken, Task> NoDelay => (_, _) => Task.CompletedTask;

    [Fact]
    public async Task AHeartbeatOfTheFollowedServer_Reloads_AndOthersDoNot()
    {
        await using var feed = new ServerLiveFeed(_hubFactory, NoDelay);
        var reloads = 0;
        await feed.StartAsync(4, ServerLiveFeedTriggers.Heartbeat, () => { reloads++; return Task.CompletedTask; });

        await feed.OnHeartbeatAsync(9);
        Assert.Equal(0, reloads);

        await feed.OnHeartbeatAsync(4);
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task HeartbeatOnly_IgnoresCompletedTasks_SoAViewThatQueuesATaskCannotLoop()
    {
        await using var feed = new ServerLiveFeed(_hubFactory, NoDelay);
        var reloads = 0;
        await feed.StartAsync(4, ServerLiveFeedTriggers.Heartbeat, () => { reloads++; return Task.CompletedTask; });

        await feed.OnTaskCompletedAsync(new TaskCompletedNotification { TaskId = 1, ServerId = 4 });

        Assert.Equal(0, reloads);
    }

    [Fact]
    public async Task HeartbeatAndTasks_ReloadsOnTheFollowedServersTasksOnly()
    {
        await using var feed = new ServerLiveFeed(_hubFactory, NoDelay);
        var reloads = 0;
        await feed.StartAsync(4, ServerLiveFeedTriggers.HeartbeatAndTasks, () => { reloads++; return Task.CompletedTask; });

        await feed.OnTaskCompletedAsync(new TaskCompletedNotification { TaskId = 1, ServerId = 5 });
        Assert.Equal(0, reloads);

        await feed.OnTaskCompletedAsync(new TaskCompletedNotification { TaskId = 2, ServerId = 4 });
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task ABurstOfPushes_IsCoalescedIntoOneReload()
    {
        var window = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var feed = new ServerLiveFeed(_hubFactory, (_, _) => window.Task);
        var reloads = 0;
        await feed.StartAsync(4, ServerLiveFeedTriggers.HeartbeatAndTasks, () => { reloads++; return Task.CompletedTask; });

        var first = feed.OnHeartbeatAsync(4);
        await feed.OnTaskCompletedAsync(new TaskCompletedNotification { TaskId = 7, ServerId = 4 });
        await feed.OnHeartbeatAsync(4);
        Assert.Equal(0, reloads);

        window.SetResult();
        await first;

        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task APushDuringAReload_SchedulesExactlyOneMore()
    {
        await using var feed = new ServerLiveFeed(_hubFactory, NoDelay);
        var reloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloads = 0;
        await feed.StartAsync(4, ServerLiveFeedTriggers.Heartbeat, async () =>
        {
            reloads++;
            if (reloads == 1) await reloading.Task;
        });

        var first = feed.OnHeartbeatAsync(4);
        await feed.OnHeartbeatAsync(4);
        await feed.OnHeartbeatAsync(4);
        reloading.SetResult();
        await first;

        Assert.Equal(2, reloads);
    }

    [Fact]
    public async Task AFailingReload_DoesNotEscape_AndTheNextPushRetries()
    {
        await using var feed = new ServerLiveFeed(_hubFactory, NoDelay);
        var attempts = 0;
        await feed.StartAsync(4, ServerLiveFeedTriggers.Heartbeat, () =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException(new HttpRequestException("down"))
                : Task.CompletedTask;
        });

        var exception = await Record.ExceptionAsync(() => feed.OnHeartbeatAsync(4));
        await feed.OnHeartbeatAsync(4);

        Assert.Null(exception);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AnUnavailableHub_LeavesTheFeedTargeted_AndSwitchingServerRetargetsIt()
    {
        await using var feed = new ServerLiveFeed(_hubFactory, NoDelay);
        var reloads = new List<int>();

        await feed.StartAsync(4, ServerLiveFeedTriggers.Heartbeat, () => { reloads.Add(4); return Task.CompletedTask; });
        Assert.Equal(4, feed.ServerId);

        await feed.StartAsync(8, ServerLiveFeedTriggers.Heartbeat, () => { reloads.Add(8); return Task.CompletedTask; });
        await feed.OnHeartbeatAsync(4);
        await feed.OnHeartbeatAsync(8);

        Assert.Equal(8, feed.ServerId);
        Assert.Equal([8], reloads);
    }

    [Fact]
    public async Task AfterDispose_PushesNoLongerReload()
    {
        var feed = new ServerLiveFeed(_hubFactory, NoDelay);
        var reloads = 0;
        await feed.StartAsync(4, ServerLiveFeedTriggers.Heartbeat, () => { reloads++; return Task.CompletedTask; });

        await feed.DisposeAsync();
        await feed.OnHeartbeatAsync(4);
        await feed.StartAsync(4, ServerLiveFeedTriggers.Heartbeat, () => { reloads++; return Task.CompletedTask; });

        Assert.Equal(0, reloads);
    }
}
