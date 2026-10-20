// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>Persistence of the external probe result on the server row (PLAN-010 lot 1).</summary>
public interface IServerReachabilityRepository
{
    /// <summary>Offline servers whose probe is missing, older than <paramref name="probedBefore"/>,
    /// or older than their last heartbeat (a result left over from an earlier offline period).</summary>
    Task<List<Server>> GetOfflineServersDueForProbeAsync(DateTime probedBefore, CancellationToken ct = default);

    /// <summary>Stores the probe classification, its time and the probed target.</summary>
    Task RecordAsync(int serverId, ServerReachability result, string? target, DateTime probedAt, CancellationToken ct = default);
}
