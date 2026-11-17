// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Services;

/// <summary>
/// A hosted worker whose work is shared across blue-green colours: it runs only while this instance holds
/// the PostgreSQL leader lease named <c>leaseName</c> (ADR-029, live-colour decision of 2026-10-02). The lease
/// is mandatory: a host that does not register <see cref="IPostgresLeaderLease"/> fails to build the worker
/// instead of running it without election.
/// <para>
/// <see cref="RunLeaderLoopAsync"/> is handed to <see cref="IPostgresLeaderLease.RunAsLeaderAsync"/> and receives
/// a token cancelled when the host stops or when leadership is lost (a severed lease connection, or a leader that
/// finds itself in reserve): the loop must observe it. When leadership comes back the lease calls the loop again,
/// so the loop restarts from its beginning.
/// </para>
/// </summary>
public abstract class LeaderBackgroundService(IPostgresLeaderLease leaderLease, string leaseName) : BackgroundService
{
    /// <summary>The lease, for a worker that also takes short per-operation locks (<see cref="IPostgresLeaderLease.RunSerializedAsync"/>).</summary>
    protected IPostgresLeaderLease LeaderLease { get; } = leaderLease;

    private readonly string _leaseName = string.IsNullOrWhiteSpace(leaseName)
        ? throw new ArgumentException("A leader worker needs a lease name.", nameof(leaseName))
        : leaseName;

    /// <summary>Runs <see cref="RunLeaderLoopAsync"/> while this instance leads. A worker that must decide first
    /// whether to run at all overrides it and calls <c>base.ExecuteAsync</c> to take part in the election.</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        LeaderLease.RunAsLeaderAsync(_leaseName, RunLeaderLoopAsync, stoppingToken);

    /// <summary>The work done while leading; it ends when the token is cancelled or when the worker has nothing left to do.</summary>
    protected abstract Task RunLeaderLoopAsync(CancellationToken stoppingToken);
}
