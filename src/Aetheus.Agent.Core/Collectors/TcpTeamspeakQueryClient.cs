// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Native <see cref="TcpClient"/> implementation of the TeamSpeak ServerQuery client (S-FEAT-11).
/// Replaces the previous <c>echo … | nc localhost &lt;port&gt;</c> shell-out: no <c>nc</c> dependency,
/// no shell parsing of the credential/command stream, and an in-process 5-second connect+read
/// timeout. The command script is expected to end with <c>quit</c>, so the server closes the
/// connection and <see cref="StreamReader.ReadToEndAsync(CancellationToken)"/> returns the full reply.
/// A failure is logged with its reason (recette R3-40) before <c>null</c> is returned; the command
/// script, which carries the credential, is never logged.
/// </summary>
public sealed class TcpTeamspeakQueryClient : ITeamspeakQueryClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly ITeamspeakQueryTransport _transport;
    private readonly ILogger<TcpTeamspeakQueryClient> _logger;

    public TcpTeamspeakQueryClient(ITeamspeakQueryTransport transport, ILogger<TcpTeamspeakQueryClient>? logger = null)
    {
        _transport = transport;
        _logger = logger ?? NullLogger<TcpTeamspeakQueryClient>.Instance;
    }

    public async Task<string?> ExecuteAsync(int port, string commands, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        var token = timeoutCts.Token;

        try
        {
            var payload = Encoding.ASCII.GetBytes(commands);
            var output = await _transport.ExchangeAsync(port, payload, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(output))
                return output;
            _logger.LogWarning("TeamSpeak ServerQuery on 127.0.0.1:{Port} closed the connection without any reply", port);
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                _logger.LogDebug("TeamSpeak ServerQuery on 127.0.0.1:{Port} cancelled by the caller", port);
            else if (ex is OperationCanceledException)
                _logger.LogWarning("TeamSpeak ServerQuery on 127.0.0.1:{Port} did not answer within {Timeout}s", port, Timeout.TotalSeconds);
            else
                _logger.LogWarning("TeamSpeak ServerQuery on 127.0.0.1:{Port} failed: {ErrorType}: {ErrorMessage}", port, ex.GetType().Name, ex.Message);
            return null;
        }
    }
}
