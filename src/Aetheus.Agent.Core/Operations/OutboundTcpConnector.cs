// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Socket-backed <see cref="IOutboundTcpConnector"/> (IPv4 or IPv6, whichever the name resolves to).</summary>
public sealed class OutboundTcpConnector : IOutboundTcpConnector
{
    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
