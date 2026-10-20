// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>The helper that reloads a monitored app's Logs and Errors tabs on
/// AppTelemetryChanged.</summary>
public sealed class EntityOperationalFeedTests : BunitContext
{
    private readonly HubConnectionFactory _hubFactory;

    public EntityOperationalFeedTests()
    {
        BunitTestHelper.RegisterServices(this);
        _hubFactory = Services.GetRequiredService<HubConnectionFactory>();
    }

    [Fact]
    public async Task AnEventOfTheFollowedResource_Reloads_AndOthersDoNot()
    {
        await using var feed = new EntityOperationalFeed(_hubFactory, (_, _) => Task.CompletedTask);
        var reloads = 0;
        await feed.StartAsync(ResourceType.Project, OperationalRealtimeEvents.AppTelemetryChanged, 12,
            () => { reloads++; return Task.CompletedTask; });

        await feed.OnEventAsync(13);
        Assert.Equal(0, reloads);

        await feed.OnEventAsync(12);
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task ARestart_RetargetsTheFeed()
    {
        await using var feed = new EntityOperationalFeed(_hubFactory, (_, _) => Task.CompletedTask);
        var reloaded = new List<int>();
        await feed.StartAsync(ResourceType.Project, OperationalRealtimeEvents.AppTelemetryChanged, 12,
            () => { reloaded.Add(12); return Task.CompletedTask; });
        await feed.StartAsync(ResourceType.Project, OperationalRealtimeEvents.AppTelemetryChanged, 20,
            () => { reloaded.Add(20); return Task.CompletedTask; });

        await feed.OnEventAsync(12);
        await feed.OnEventAsync(20);

        Assert.Equal([20], reloaded);
    }

    [Fact]
    public async Task ABurstOfIngestionBatches_IsCoalescedIntoOneReload()
    {
        var window = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var feed = new EntityOperationalFeed(_hubFactory, (_, _) => window.Task);
        var reloads = 0;
        await feed.StartAsync(ResourceType.Project, OperationalRealtimeEvents.AppTelemetryChanged, 12,
            () => { reloads++; return Task.CompletedTask; });

        var first = feed.OnEventAsync(12);
        for (var i = 0; i < 5; i++)
            await feed.OnEventAsync(12);
        window.SetResult();
        await first;

        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task AFailingReload_DoesNotEscape()
    {
        await using var feed = new EntityOperationalFeed(_hubFactory, (_, _) => Task.CompletedTask);
        await feed.StartAsync(ResourceType.Project, OperationalRealtimeEvents.AppTelemetryChanged, 12,
            () => Task.FromException(new HttpRequestException("down")));

        Assert.Null(await Record.ExceptionAsync(() => feed.OnEventAsync(12)));
    }

    [Fact]
    public async Task AfterDispose_EventsNoLongerReload()
    {
        var feed = new EntityOperationalFeed(_hubFactory, (_, _) => Task.CompletedTask);
        var reloads = 0;
        await feed.StartAsync(ResourceType.Project, OperationalRealtimeEvents.AppTelemetryChanged, 12,
            () => { reloads++; return Task.CompletedTask; });

        await feed.DisposeAsync();
        await feed.OnEventAsync(12);

        Assert.Equal(0, reloads);
    }
}
