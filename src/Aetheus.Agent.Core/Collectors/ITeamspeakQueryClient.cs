// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors;

/// <summary>Runs TeamSpeak ServerQuery command scripts through a native TCP socket with in-process
/// connection and read timeouts, without a shell or external transport binary.</summary>
public interface ITeamspeakQueryClient
{
    /// <summary>
    /// Connects to the ServerQuery telnet endpoint on <paramref name="port"/> (localhost), writes the
    /// newline-terminated <paramref name="commands"/> script (which must end with <c>quit</c> so the
    /// server closes the connection), and returns everything the server sent. Returns <c>null</c> on
    /// any connection/timeout failure.
    /// </summary>
    Task<string?> ExecuteAsync(int port, string commands, CancellationToken ct = default);
}
