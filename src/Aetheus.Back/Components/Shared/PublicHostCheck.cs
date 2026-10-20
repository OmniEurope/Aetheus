// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;

namespace Aetheus.Back.Components.Shared;

/// <summary>
/// Tells whether a host names only public addresses (<see cref="WebhookSsrfGuard"/>), the fire-time
/// check of outbound webhooks and notification channels. An address literal is judged as is; a name is
/// resolved and every answer must be public: no answer, a resolution failure or one forbidden address
/// among public ones refuses it. The resolver is injected so the check can be exercised without DNS;
/// <see cref="Default"/> resolves through <see cref="Dns"/>.
/// </summary>
public sealed class PublicHostCheck(Func<string, CancellationToken, Task<IPAddress[]>> resolve)
{
    /// <summary>The check over the system resolver.</summary>
    public static PublicHostCheck Default { get; } = new((host, ct) => Dns.GetHostAddressesAsync(host, ct));

    public async Task<bool> IsPublicAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return !WebhookSsrfGuard.IsForbiddenAddress(literal);

        IPAddress[] addresses;
        try
        {
            addresses = await resolve(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return false;
        }

        return addresses.Length > 0 && !addresses.Any(WebhookSsrfGuard.IsForbiddenAddress);
    }
}
