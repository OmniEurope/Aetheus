// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// A page that asks "used by" once when it opens is told when a pipeline is created, updated or deleted: it joins the
/// entity updates group, reacts to the pipeline events only, joins again after a reconnect, and leaves on dispose.
/// </summary>
public sealed class PipelineChangeSubscriptionTests : BunitContext
{
    private int _changes;
    private readonly SemaphoreSlim _changed = new(0);

    public PipelineChangeSubscriptionTests()
    {
        BunitTestHelper.RegisterServices(this);
        this.UseInMemoryHubs();
    }

    private InMemoryHubServer Entities => this.Hub("entities");

    private PipelineChangeSubscription NewSubscription() =>
        new(Services.GetRequiredService<HubConnectionFactory>(), () =>
        {
            Interlocked.Increment(ref _changes);
            _changed.Release();
            return Task.CompletedTask;
        });

    private int Changes => Volatile.Read(ref _changes);

    [Fact]
    public async Task Starting_JoinsTheEntityUpdatesOfPipelines()
    {
        await using var subscription = NewSubscription();

        await subscription.StartAsync();

        await Entities.WhenInvokedAsync("JoinEntityUpdates");
        Assert.Equal([(int)ResourceType.Pipeline], Entities.IntArguments("JoinEntityUpdates"));
        Assert.Equal(0, Changes);
    }

    [Fact]
    public async Task APipelineEvent_RunsTheCallback_ButAnotherResourceDoesNot()
    {
        await using var subscription = NewSubscription();
        await subscription.StartAsync();

        await Entities.PushAsync("EntityChanged", ResourceType.Vault, 4, "Updated");
        Assert.Equal(0, Changes);

        await Entities.PushAsync("EntityChanged", ResourceType.Pipeline, 9, "Created");
        Assert.Equal(1, Changes);

        await Entities.PushAsync("EntityChanged", ResourceType.Pipeline, 9, "Deleted");
        Assert.Equal(2, Changes);
    }

    [Fact]
    public async Task AfterAReconnect_TheGroupIsJoinedAgain_AndTheCallbackReadsAgain()
    {
        await using var subscription = NewSubscription();
        await subscription.StartAsync();
        await Entities.WhenInvokedAsync("JoinEntityUpdates");

        await Entities.DropAsync();

        await Entities.WhenInvokedAsync("JoinEntityUpdates", 2);
        // The callback runs on the client, which the hub server never sees: it signals the test itself. The bound only
        // keeps a broken rejoin from hanging the run.
        Assert.True(await _changed.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken),
            "The callback never ran after the rejoin.");
        Assert.Equal(1, Changes);
        Assert.Equal(2, Entities.Count("JoinEntityUpdates"));
    }

    [Fact]
    public async Task Disposing_LeavesTheGroup_Once()
    {
        var subscription = NewSubscription();
        await subscription.StartAsync();
        await Entities.WhenInvokedAsync("JoinEntityUpdates");

        await subscription.DisposeAsync();
        await subscription.DisposeAsync();

        Assert.Equal([(int)ResourceType.Pipeline], Entities.IntArguments("LeaveEntityUpdates"));
    }

    [Fact]
    public async Task Disposing_WithoutHavingStarted_DoesNothing()
    {
        var subscription = NewSubscription();

        await subscription.DisposeAsync();

        Assert.Equal(0, Entities.Count("LeaveEntityUpdates"));
    }

    [Fact]
    public async Task AnUnavailableHub_LeavesThePageOnWhatItLoaded_WithoutThrowing()
    {
        Entities.RefuseConnections = true;
        await using var subscription = NewSubscription();

        await subscription.StartAsync();

        Assert.Equal(0, Entities.Count("JoinEntityUpdates"));
        Assert.Equal(0, Changes);
    }
}
