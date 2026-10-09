// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>Opens an outbound TCP connection. An interface so the probes that use it run in tests without
/// touching the network.</summary>
public interface IOutboundTcpConnector
{
    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>; the returned stream owns the socket.</summary>
    Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken);
}
