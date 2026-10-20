// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Real TCP prober: a connect followed by an immediate close. Nothing is written to the socket, so
/// no protocol is spoken and no credential can leave the backend. The try/catch blocks are mandatory
/// here: a refused or timed-out connect surfaces only as an exception, and each one is the answer.
/// </summary>
public sealed class TcpReachabilityProber(TimeProvider timeProvider) : ITcpReachabilityProber
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostname, CancellationToken ct)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(hostname, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    public async Task<TcpProbeOutcome> ConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), linked.Token).ConfigureAwait(false);
            return TcpProbeOutcome.Open;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TcpProbeOutcome.NoAnswer;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return TcpProbeOutcome.Refused;
        }
        catch (SocketException)
        {
            return TcpProbeOutcome.NoAnswer;
        }
    }
}
