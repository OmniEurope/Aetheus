// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Collectors;

/// <summary>Whether Apache is running and its main process id, as read right now.</summary>
public readonly record struct ApacheRunState(bool IsRunning, int? Pid);

public interface IApacheCollector
{
    Task<ApacheDataDto> CollectAsync(CancellationToken ct = default);

    /// <summary>
    /// Recette R-17: only the running state, cheap enough to read on every heartbeat while the rest of the
    /// inventory stays cached. Null when the state could not be read (the cached one then stands, instead of a
    /// failed probe reporting Apache stopped).
    /// </summary>
    Task<ApacheRunState?> CollectRunStateAsync(CancellationToken ct = default);
}
