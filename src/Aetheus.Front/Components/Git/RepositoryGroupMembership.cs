// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Git;

/// <summary>
/// The git hub groups a page has joined, one per repository it shows. A sync brings the joined set in
/// line with the repositories shown at the moment it runs; syncs never overlap, so two reloads that end
/// close together cannot both read the same joined set and leave or join the same group twice, and the
/// last reload decides what is joined.
/// </summary>
internal sealed class RepositoryGroupMembership(Func<string, int, Task> invoke)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<int> _joined = [];
    private volatile bool _disposed;

    /// <summary>Leaves the groups of repositories no longer shown and joins those newly shown. The shown
    /// repositories are read once the previous sync has ended, so a sync that waited joins what is shown
    /// now, not what was shown when it was requested. Nothing is done while <paramref name="isConnected"/>
    /// is false, read once the sync holds its turn, and after <see cref="DisposeAsync"/>. A hub call that fails
    /// propagates to the caller; the joined set only holds the groups whose call succeeded, so the next sync
    /// retries exactly the groups not yet joined or left.</summary>
    public async Task SyncAsync(Func<IReadOnlyCollection<int>> shown, Func<bool> isConnected)
    {
        if (_disposed) return;
        await _gate.WaitAsync();
        try
        {
            if (_disposed || !isConnected()) return;
            var visible = shown().ToHashSet();
            foreach (var repositoryId in _joined.Except(visible).ToList())
            {
                if (_disposed) return;
                await invoke("LeaveRepositoryGroup", repositoryId);
                _joined.Remove(repositoryId);
            }
            foreach (var repositoryId in visible.Except(_joined).ToList())
            {
                if (_disposed) return;
                await invoke("JoinRepositoryGroup", repositoryId);
                _joined.Add(repositoryId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets every joined group: group membership belongs to a connection, so a new
    /// connection starts with none. Waits for a sync in progress, which would otherwise add to the set
    /// after it was emptied.</summary>
    public async Task ForgetAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _joined.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the membership: waits for a sync in progress, forgets every joined group, and makes
    /// every later sync a no-op, so a sync still queued when the page is left never calls the hub the page
    /// is disposing. The gate is not disposed: a sync queued behind this call would release a disposed
    /// semaphore (a SemaphoreSlim holds no operating-system handle unless its wait handle is requested).</summary>
    public async Task DisposeAsync()
    {
        _disposed = true;
        await ForgetAllAsync();
    }
}
