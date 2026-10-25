// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.Services;
using Aetheus.Front.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// A run page's live connection against a real hub connection on an in-memory hub: the run group and each
/// child run group are joined once, a child that left is left, a refused join is tried again on the next
/// pass, a group that cannot be left stays joined; and while the hub is unreachable a running run is shown
/// interrupted after the threshold, reloaded and retried every fallback interval until the hub is back.
/// </summary>
public sealed class PipelineRunLiveConnectionGroupsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryHubConnectionFactory _factory =
        new(new AuthStateProvider(new FakeJsRuntime(), NullLogger<AuthStateProvider>.Instance));
    private readonly List<int> _children = [];

    private InMemoryHubServer Hub => _factory.Server("pipelines");

    private readonly StateSignal _ticked = new();
    private int _fallbackPasses;

    // The poll loop ends each tick of its clock with one fallback pass on the page's dispatcher; counting
    // the passes that finished tells the test a tick was fully handled, without waiting on the clock.
    private async Task DispatchAsync(Func<Task> callback)
    {
        await callback();
        if (callback.Method.Name != "FallBackWhileInterruptedAsync") return;
        Interlocked.Increment(ref _fallbackPasses);
        _ticked.Pulse();
    }

    private Task TickHandledAsync(int passes) =>
        _ticked.WhenAsync(() => Volatile.Read(ref _fallbackPasses) >= passes, $"{passes} fallback pass(es)");

    private PipelineRunLiveConnection Connection(
        Func<Task>? onReload = null, Action? onLiveStateChanged = null, TimeProvider? clock = null) =>
        new(_factory, NullLogger.Instance, 90, () => _children,
            onReload ?? (() => Task.CompletedTask), () => Task.CompletedTask, DispatchAsync,
            runInProgress: () => true, onLiveStateChanged: onLiveStateChanged, timeProvider: clock);

    [Fact]
    public async Task TheRunAndItsChildren_AreJoinedOnce_AndAChildThatLeftIsLeft()
    {
        _children.AddRange([91, 92]);
        await using var connection = Connection();
        await connection.StartAsync();

        Assert.Equal([90, 91, 92], Hub.IntArguments("JoinPipelineRunGroup").Order());

        _children.Remove(91);
        _children.Add(93);
        await connection.JoinGroupsAsync();

        Assert.Equal([91], Hub.IntArguments("LeavePipelineRunGroup"));
        Assert.Equal([90, 91, 92, 93], Hub.IntArguments("JoinPipelineRunGroup").Order());
    }

    /// <summary>R1-12: the server announces an analysis report publication per run; the connection says
    /// "publishing" from the first announcement of any run of the tree until the last one ends, and tells
    /// the page each change so it can veil its timeline.</summary>
    [Fact]
    public async Task AnAnalysisPublication_IsReportedWhileAnyRunOfTheTreePublishes()
    {
        var changes = 0;
        await using var connection = Connection(onLiveStateChanged: () => Interlocked.Increment(ref changes));
        await connection.StartAsync();
        Assert.False(connection.AnalysisPublishing);

        await Hub.PushAsync("AnalysisPublishing", 90, true);
        Assert.True(connection.AnalysisPublishing);
        await Hub.PushAsync("AnalysisPublishing", 91, true);
        await Hub.PushAsync("AnalysisPublishing", 90, false);
        Assert.True(connection.AnalysisPublishing);

        await Hub.PushAsync("AnalysisPublishing", 91, false);
        Assert.False(connection.AnalysisPublishing);
        Assert.Equal(4, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task ARefusedJoin_IsTriedAgainOnTheNextPass()
    {
        Hub.Refuse("JoinPipelineRunGroup");
        await using var connection = Connection();
        await connection.StartAsync();
        Assert.Equal([90], Hub.IntArguments("JoinPipelineRunGroup"));

        Hub.Accept("JoinPipelineRunGroup");
        await connection.JoinGroupsAsync();
        await connection.JoinGroupsAsync();

        Assert.Equal([90, 90], Hub.IntArguments("JoinPipelineRunGroup"));
    }

    [Fact]
    public async Task AGroupThatCannotBeLeft_StaysJoined_AndIsLeftOnceItCanBe()
    {
        _children.Add(91);
        await using var connection = Connection();
        await connection.StartAsync();
        Hub.Refuse("LeavePipelineRunGroup");
        _children.Clear();

        await connection.JoinGroupsAsync();
        Hub.Accept("LeavePipelineRunGroup");
        await connection.JoinGroupsAsync();
        await connection.JoinGroupsAsync();

        Assert.Equal([91, 91], Hub.IntArguments("LeavePipelineRunGroup"));
        Assert.Equal(2, Hub.Count("JoinPipelineRunGroup"));
    }

    [Fact]
    public async Task WithoutAConnectedHub_NoGroupIsJoined()
    {
        Hub.RefuseConnections = true;
        await using var connection = Connection();
        await connection.StartAsync();

        await connection.JoinGroupsAsync();

        Assert.Empty(Hub.Invocations);
    }

    [Fact]
    public async Task AnUnreachableHub_IsShownInterrupted_RetriedEveryInterval_AndRejoinedOnceBack()
    {
        Hub.RefuseConnections = true;
        var clock = new FakeTimeProvider(T0);
        var stateChanges = 0;
        var reloads = 0;
        await using var connection = Connection(
            onReload: () => { Interlocked.Increment(ref reloads); return Task.CompletedTask; },
            onLiveStateChanged: () => Interlocked.Increment(ref stateChanges),
            clock: clock);
        await connection.StartAsync();
        Assert.Equal(1, Hub.ConnectAttempts);

        // Under the threshold a tick shows nothing and retries nothing.
        clock.Advance(TimeSpan.FromSeconds(5));
        await TickHandledAsync(1);
        Assert.Equal((0, 1), (Volatile.Read(ref stateChanges), Hub.ConnectAttempts));

        // At the threshold the interruption is shown and the connection is retried.
        clock.Advance(TimeSpan.FromSeconds(5));
        await TickHandledAsync(2);
        Assert.Equal((1, 2), (Volatile.Read(ref stateChanges), Hub.ConnectAttempts));
        Assert.Equal(T0, connection.InterruptedSince);

        // Within the fallback interval no new attempt is made.
        clock.Advance(TimeSpan.FromSeconds(5));
        await TickHandledAsync(3);
        Assert.Equal(2, Hub.ConnectAttempts);

        // The hub is back: the next retry connects, joins the run group and clears the interruption.
        Hub.RefuseConnections = false;
        clock.Advance(TimeSpan.FromSeconds(10));
        await TickHandledAsync(4);
        await Hub.WhenInvokedAsync("JoinPipelineRunGroup");
        Assert.Equal(2, Volatile.Read(ref stateChanges));
        Assert.Null(connection.InterruptedSince);
        Assert.Equal([90], Hub.IntArguments("JoinPipelineRunGroup"));
    }
}
