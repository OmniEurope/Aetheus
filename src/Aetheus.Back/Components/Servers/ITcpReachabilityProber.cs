// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Network edge of the external probe, kept behind an interface so the classification is tested
/// without touching the network. The real implementation only opens and closes TCP connections.
/// </summary>
public interface ITcpReachabilityProber
{
    /// <summary>Resolves a hostname; returns an empty list when it does not resolve.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostname, CancellationToken ct);

    /// <summary>Opens a TCP connection to <paramref name="address"/>:<paramref name="port"/> and closes
    /// it at once, without sending anything.</summary>
    Task<TcpProbeOutcome> ConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct);
}
