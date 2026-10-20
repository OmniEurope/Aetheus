// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.Services;
using Aetheus.Front.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// The pipeline edit page's live connection against a real hub connection on an in-memory hub: it joins
/// the pipeline updates group and the group of each active run, leaves the group of a run no longer
/// active (even when the hub refuses the leave), tries a refused join again, rejoins everything after a
/// reconnect, and reloads on a run of its own pipeline only.
/// </summary>
public sealed class PipelineRunsLiveConnectionTests
{
    private readonly InMemoryHubConnectionFactory _factory =
        new(new AuthStateProvider(new FakeJsRuntime(), NullLogger<AuthStateProvider>.Instance));
    private readonly List<int> _active = [];
    private readonly StateSignal _reloaded = new();
    private int _reloads;
    // Every call the connection makes on the page's dispatcher: a handler that decides to reload goes
    // through it, one that ignores the event does not.
    private int _dispatches;

    private InMemoryHubServer Hub => _factory.Server("pipelines");

    private PipelineRunsLiveConnection Connection() =>
        new(_factory, NullLogger.Instance, 5, () => _active,
            () => { Interlocked.Increment(ref _reloads); _reloaded.Pulse(); return Task.CompletedTask; },
            callback => { Interlocked.Increment(ref _dispatches); return callback(); });

    private Task ReloadedAsync(int count) =>
        _reloaded.WhenAsync(() => Volatile.Read(ref _reloads) >= count, $"{count} reload(s)");

    [Fact]
    public async Task TheActiveRuns_AreJoined_AndARunNoLongerActiveIsLeft()
    {
        _active.AddRange([11, 12]);
        await using var connection = Connection();
        await connection.StartAsync();

        Assert.Equal(1, Hub.Count("JoinPipelineUpdatesGroup"));
        Assert.Equal([11, 12], Hub.IntArguments("JoinPipelineRunGroup").Order());

        _active.Remove(11);
        _active.Add(13);
        await connection.SyncGroupsAsync();

        Assert.Equal([11], Hub.IntArguments("LeavePipelineRunGroup"));
        Assert.Equal([11, 12, 13], Hub.IntArguments("JoinPipelineRunGroup").Order());
    }

    [Fact]
    public async Task ARefusedJoin_IsTriedAgain_AndARefusedLeaveIsNotRepeated()
    {
        _active.Add(11);
        Hub.Refuse("JoinPipelineRunGroup");
        await using var connection = Connection();
        await connection.StartAsync();

        Hub.Accept("JoinPipelineRunGroup");
        await connection.SyncGroupsAsync();
        Assert.Equal([11, 11], Hub.IntArguments("JoinPipelineRunGroup"));

        Hub.Refuse("LeavePipelineRunGroup");
        _active.Clear();
        await connection.SyncGroupsAsync();
        await connection.SyncGroupsAsync();

        Assert.Equal([11], Hub.IntArguments("LeavePipelineRunGroup"));
    }

    [Fact]
    public async Task WithoutAConnectedHub_NothingIsJoined()
    {
        _active.Add(11);
        Hub.RefuseConnections = true;
        await using var connection = Connection();
        await connection.StartAsync();

        await connection.SyncGroupsAsync();

        Assert.Empty(Hub.Invocations);
    }

    [Fact]
    public async Task AfterAReconnect_EveryGroupIsJoinedAgain_AndThePageReloads()
    {
        _active.Add(11);
        await using var connection = Connection();
        await connection.StartAsync();

        await Hub.DropAsync();

        await Hub.WhenAsync(
            () => Hub.Count("JoinPipelineUpdatesGroup") >= 2 && Hub.Count("JoinPipelineRunGroup") >= 2,
            "the groups joined again");
        await ReloadedAsync(1);
        Assert.Equal(2, Hub.Count("JoinPipelineUpdatesGroup"));
        Assert.Equal([11, 11], Hub.IntArguments("JoinPipelineRunGroup"));
    }

    [Fact]
    public async Task ARunStartedForThisPipeline_Reloads_AnotherPipelinesRunDoesNot()
    {
        await using var connection = Connection();
        await connection.StartAsync();
        var dispatches = Volatile.Read(ref _dispatches);

        // The push completes once the handler has run: it decided not to schedule anything.
        await Hub.PushAsync("PipelineRunStarted", 40, 6);
        Assert.Equal(dispatches, Volatile.Read(ref _dispatches));
        Assert.Equal(0, Volatile.Read(ref _reloads));

        await Hub.PushAsync("PipelineRunStarted", 41, 5);
        Assert.Equal(dispatches + 1, Volatile.Read(ref _dispatches));
        await ReloadedAsync(1);
        Assert.Equal(1, Volatile.Read(ref _reloads));
    }
}
