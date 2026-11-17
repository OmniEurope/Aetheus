// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests;

/// <summary>
/// A hand-written <see cref="IPostgresLeaderLease"/> for tests, compiled into both Back test projects: a leader
/// runs the leader work at once, in the caller's task, with the caller's token; a non-leader never runs it.
/// Serialized operations run at once in both. It never opens a database connection.
/// </summary>
internal sealed class LeaderLeaseDouble(bool leads) : IPostgresLeaderLease
{
    private int _leaderRequests;
    private int _serializedRequests;

    /// <summary>A lease that is leader: its leader work runs.</summary>
    public static LeaderLeaseDouble Leader() => new(true);

    /// <summary>A lease that is never leader: its leader work never runs.</summary>
    public static LeaderLeaseDouble NotLeader() => new(false);

    /// <summary>How many times a worker asked to lead.</summary>
    public int LeaderRequests => Volatile.Read(ref _leaderRequests);

    /// <summary>How many serialized operations were requested.</summary>
    public int SerializedRequests => Volatile.Read(ref _serializedRequests);

    /// <summary>The name of the last lease a worker asked to lead under.</summary>
    public string? LastLeaseName { get; private set; }

    public Task RunAsLeaderAsync(string leaseName, Func<CancellationToken, Task> leaderWork, CancellationToken stoppingToken)
    {
        LastLeaseName = leaseName;
        Interlocked.Increment(ref _leaderRequests);
        return leads ? leaderWork(stoppingToken) : Task.CompletedTask;
    }

    public Task RunSerializedAsync(string operationName, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _serializedRequests);
        return operation(cancellationToken);
    }
}
