// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Git;

namespace Aetheus.Front.Tests.Pages.Git;

/// <summary>
/// The groups a repositories page has joined stay right when reloads overlap. Every case drives the
/// membership with hub calls it holds back itself, so the overlap is made by the test and nothing here
/// waits for the clock: a sync runs synchronously up to its first unfinished hub call, so what a second
/// sync does while the first is held is already visible when it is started.
/// </summary>
public sealed class RepositoryGroupMembershipTests
{
    private readonly List<string> _calls = [];
    private TaskCompletionSource? _heldLeave;
    private TaskCompletionSource? _heldJoin;

    private RepositoryGroupMembership NewMembership() => new(async (target, repositoryId) =>
    {
        lock (_calls) _calls.Add($"{target}:{repositoryId}");
        var held = target == "LeaveRepositoryGroup" ? _heldLeave : _heldJoin;
        if (held is not null) await held.Task;
    });

    private string[] Calls()
    {
        lock (_calls) return [.. _calls];
    }

    private void ClearCalls()
    {
        lock (_calls) _calls.Clear();
    }

    private static Task Sync(RepositoryGroupMembership membership, Func<int[]> shown, bool connected = true) =>
        membership.SyncAsync(() => shown(), () => connected);

    [Fact]
    public async Task TwoOverlappingReloads_LeaveARepositoryNoLongerShownOnce()
    {
        var membership = NewMembership();
        await Sync(membership, () => [1, 2]);
        ClearCalls();
        _heldLeave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Sync(membership, () => [2, 3]);
        var second = Sync(membership, () => [2, 3]);

        Assert.Equal(["LeaveRepositoryGroup:1"], Calls());
        _heldLeave.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(["LeaveRepositoryGroup:1", "JoinRepositoryGroup:3"], Calls());
    }

    [Fact]
    public async Task TwoOverlappingReloads_JoinANewRepositoryOnce()
    {
        var membership = NewMembership();
        await Sync(membership, () => [1]);
        ClearCalls();
        _heldJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Sync(membership, () => [1, 2]);
        var second = Sync(membership, () => [1, 2]);

        Assert.Equal(["JoinRepositoryGroup:2"], Calls());
        _heldJoin.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(["JoinRepositoryGroup:2"], Calls());
    }

    [Fact]
    public async Task ASyncThatWaited_JoinsWhatIsShownWhenItRuns_NotWhenItWasRequested()
    {
        var membership = NewMembership();
        await Sync(membership, () => [1, 2]);
        ClearCalls();
        _heldLeave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int[] shown = [2, 3];

        var first = Sync(membership, () => shown);
        var second = Sync(membership, () => shown);
        shown = [3, 4];
        _heldLeave.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(
            ["LeaveRepositoryGroup:1", "JoinRepositoryGroup:3", "LeaveRepositoryGroup:2", "JoinRepositoryGroup:4"],
            Calls());
        ClearCalls();
        await Sync(membership, () => [3, 4]);
        Assert.Empty(Calls());
    }

    [Fact]
    public async Task AfterForgettingEveryGroup_TheShownRepositoriesAreJoinedAgain()
    {
        var membership = NewMembership();
        await Sync(membership, () => [1, 2]);
        ClearCalls();

        await membership.ForgetAllAsync();
        await Sync(membership, () => [1, 2]);

        Assert.Equal(["JoinRepositoryGroup:1", "JoinRepositoryGroup:2"], Calls());
    }

    [Fact]
    public async Task ForgettingEveryGroup_WaitsForASyncInProgress()
    {
        var membership = NewMembership();
        await Sync(membership, () => [1]);
        ClearCalls();
        _heldJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = Sync(membership, () => [1, 2]);

        var forget = membership.ForgetAllAsync();

        Assert.False(forget.IsCompleted);
        _heldJoin.SetResult();
        await Task.WhenAll(sync, forget);
        ClearCalls();
        await Sync(membership, () => [1, 2]);
        Assert.Equal(["JoinRepositoryGroup:1", "JoinRepositoryGroup:2"], Calls());
    }

    [Fact]
    public async Task WithoutAConnection_NoGroupIsJoinedOrLeft()
    {
        var membership = NewMembership();

        await Sync(membership, () => [1, 2], connected: false);

        Assert.Empty(Calls());
    }

    [Fact]
    public async Task AFailedJoin_PropagatesAndLeavesTheGroupNotJoined_SoTheNextSyncRetriesIt()
    {
        var failing = true;
        var membership = new RepositoryGroupMembership((target, repositoryId) =>
        {
            lock (_calls) _calls.Add($"{target}:{repositoryId}");
            return failing && repositoryId == 2 ? Task.FromException(new InvalidOperationException("hub down")) : Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Sync(membership, () => [1, 2]));
        failing = false;
        ClearCalls();
        await Sync(membership, () => [1, 2]);

        Assert.Equal(["JoinRepositoryGroup:2"], Calls());
    }

    [Fact]
    public async Task AfterDisposal_AQueuedSyncCallsNoHub_AndTheGateStaysUsable()
    {
        var membership = NewMembership();
        await Sync(membership, () => [1]);
        ClearCalls();
        _heldJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = Sync(membership, () => [1, 2]);
        var queued = Sync(membership, () => [1, 2, 3]);

        var dispose = membership.DisposeAsync();
        _heldJoin.SetResult();
        await Task.WhenAll(inFlight, queued, dispose);
        await Sync(membership, () => [1, 2, 3, 4]);

        Assert.Equal(["JoinRepositoryGroup:2"], Calls());
    }
}
