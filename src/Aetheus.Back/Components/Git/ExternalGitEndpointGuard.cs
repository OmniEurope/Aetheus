// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Decides whether the backend may contact an external repository, and pins the address it checked.
/// A project writer chooses the base URL of a mirror, so without this the backend's <c>ls-remote</c>,
/// <c>clone --mirror</c> and <c>fetch</c> could be aimed at, or redirected to, an internal service
/// (SSRF found beside audit BACK-024). Same rules as <c>GitCliService</c>: every DNS answer must
/// be public, HTTPS redirects are refused and the checked address is pinned, so a second resolution
/// cannot swap it. Hosts in <see cref="GitLightOptions.AllowedInternalHosts"/> are exempt from the
/// address check, for a forge on the operator's own network.
/// </summary>
public sealed class ExternalGitEndpointGuard(
    GitLightOptions options,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve =
        resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));

    /// <summary>
    /// The <c>git -c</c> arguments that confine one invocation to the checked endpoint, or a refusal.
    /// SSH (<c>git@host:path</c>) has no redirect and no curl resolve option: its host is checked, but
    /// not pinned.
    /// </summary>
    public async Task<(IReadOnlyList<string> GitConfigArgs, string? Refusal)> CheckAsync(string remoteUrl, CancellationToken ct)
    {
        var (host, port, isHttps) = Parse(remoteUrl);
        if (host is null)
            return ([], "The remote URL has no host.");

        IReadOnlyList<string> noRedirect = isHttps ? ["-c", "http.followRedirects=false"] : [];
        if (options.AllowedInternalHosts.Any(allowed => string.Equals(allowed.Trim(), host, StringComparison.OrdinalIgnoreCase)))
            return (noRedirect, null);

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? [literal]
                : await _resolve(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return ([], $"The host '{host}' does not resolve.");
        }

        // A mixed answer is refused too: one public address beside a private one still lets address
        // selection or a retry cross the boundary.
        if (addresses.Length == 0 || addresses.Any(WebhookSsrfGuard.IsForbiddenAddress))
            return ([], $"The host '{host}' resolves to a private or reserved address. An operator can allow a forge on the local network with GitLight:AllowedInternalHosts.");

        if (!isHttps)
            return ([], null);

        var selected = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var address = selected.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{selected}]" : selected.ToString();
        return ([.. noRedirect, "-c", $"http.curloptResolve={host}:{port}:{address}"], null);
    }

    private static (string? Host, int Port, bool IsHttps) Parse(string remoteUrl)
    {
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh")
            return (uri.Host, uri.IsDefaultPort ? (uri.Scheme == "http" ? 80 : 443) : uri.Port, uri.Scheme is "https" or "http");

        // scp-like SSH: user@host:path
        var at = remoteUrl.IndexOf('@');
        var colon = remoteUrl.IndexOf(':', Math.Max(at, 0));
        return at >= 0 && colon > at + 1 ? (remoteUrl[(at + 1)..colon], 22, false) : (null, 0, false);
    }
}
