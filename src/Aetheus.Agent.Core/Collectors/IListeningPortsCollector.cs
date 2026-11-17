// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors;

/// <summary>Which TCP and UDP ports are actually listening on this host, and who holds them.</summary>
public interface IListeningPortsCollector
{
    /// <summary>The listening ports, with the protocols whose probe really succeeded and when. Prefer this
    /// to <see cref="CollectAsync"/> whenever the result is reported as a scan: an empty list alone cannot
    /// tell "nothing listens" from "the probe failed".</summary>
    Task<ListeningPortsScan> ScanAsync(CancellationToken ct = default);

    /// <summary>Only the listing, for callers that read it as a best-effort hint.</summary>
    Task<List<ObservedPortDto>> CollectAsync(CancellationToken ct = default);
}
