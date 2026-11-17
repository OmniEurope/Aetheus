// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Git;

public sealed partial class GitCliService(ILogger<GitCliService> logger) : IGitCliService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Strips userinfo (user:token@) from any scheme://… URL so a credential-bearing repository URL
    // (or a git stderr line echoing it) never reaches the log file. NEVER log secret values.
    [GeneratedRegex(@"://[^@/\s]+@")]
    private static partial Regex UserInfoRegex();

    private static string StripCredentials(string text) => UserInfoRegex().Replace(text, "://");

    public async Task<List<(string BranchName, string Version)>> ListReleaseBranchesAsync(string repositoryUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);

        // Hardening (High #12): only allow http(s)/ssh schemes. Reject anything that could be
        // interpreted as a git option (`-c …`, `--upload-pack=…`) or as a `file://` / loopback
        // SSRF target.
        if (!IsRepositoryUrlAllowed(repositoryUrl))
        {
            logger.LogWarning("Rejected disallowed repository URL");
            return [];
        }

        var pinnedEndpoint = await ResolveSafeEndpointAsync(repositoryUrl, ct).ConfigureAwait(false);
        if (pinnedEndpoint is null)
        {
            logger.LogWarning("Rejected repository URL whose DNS resolved to a forbidden or unavailable address");
            return [];
        }

        return await RunListReleaseBranchesAsync(
            BuildListReleaseBranchesStartInfo(repositoryUrl, pinnedEndpoint.Value), repositoryUrl, Timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the <c>ls-remote</c> of the release branches and parses its answer, or logs and
    /// returns nothing: no answer within <paramref name="timeout"/> (the process is killed) or a failed run.
    /// A cancellation by the caller propagates.</summary>
    internal async Task<List<(string BranchName, string Version)>> RunListReleaseBranchesAsync(
        ProcessStartInfo psi, string repositoryUrl, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        string output;
        string error;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            error = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SafeKill(process);
            logger.LogWarning("git ls-remote timed out after {Timeout}s for {Url}", timeout.TotalSeconds, StripCredentials(repositoryUrl));
            return [];
        }

        if (process.ExitCode != 0)
        {
            logger.LogWarning("git ls-remote failed (exit {Code}) for {Url}: {Error}", process.ExitCode, StripCredentials(repositoryUrl), StripCredentials(error));
            return [];
        }

        return ParseOutput(output);
    }

    private static List<(string BranchName, string Version)> ParseOutput(string output)
    {
        var results = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(output))
            return results;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Format: "<hash>\trefs/heads/release/<version>"
            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0) continue;

            var refName = line[(tabIndex + 1)..];
            const string prefix = "refs/heads/";
            if (!refName.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var branchName = refName[prefix.Length..]; // "release/v1.2.0"

            const string releasePrefix = "release/";
            if (!branchName.StartsWith(releasePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var version = branchName[releasePrefix.Length..];
            // Strip leading 'v' or 'V'
            if (version.Length > 0 && (version[0] == 'v' || version[0] == 'V'))
                version = version[1..];

            if (!string.IsNullOrWhiteSpace(version))
                results.Add((branchName, version));
        }

        return results;
    }

    private static void SafeKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
        catch (NotSupportedException) { /* not supported on this platform */ }
    }

    private static bool IsRepositoryUrlAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        // External Git operations are HTTPS-only. SSH cannot enforce the same transport policy and
        // a plain HTTP clone would expose credentials and source in transit.
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "https") return false;

        // Block obvious loopback / link-local / private targets at URL level. The agent that
        // ultimately resolves DNS may have additional protections; this is a first cheap pass.
        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.StartsWith("127.", StringComparison.Ordinal)) return false;
        if (host == "::1") return false;

        // Reject literal private/link-local IPs (RFC-1918, 169.254/16 incl. cloud metadata 169.254.169.254,
        // and IPv6 unique-local fc00::/7 / link-local fe80::/10). Host may be bracketed for IPv6 - Uri.Host
        // already strips the brackets.
        if (IPAddress.TryParse(host, out var ip) && WebhookSsrfGuard.IsForbiddenAddress(ip)) return false;

        return true;
    }

    internal static ProcessStartInfo BuildListReleaseBranchesStartInfo(string repositoryUrl, (string Host, int Port, string Address) endpoint)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // -c protocol.allow=user-configured constrains the allowed transports for this invocation.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("protocol.file.allow=never");
        // Keep TLS/SNI validation against the original host while pinning libcurl to the exact
        // address validated above. DNS rebinding between validation and git's connect is impossible.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"http.curloptResolve={endpoint.Host}:{endpoint.Port}:{endpoint.Address}");
        // A redirect would leave the pinned public address for a target never validated.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("http.followRedirects=false");
        psi.ArgumentList.Add("ls-remote");
        psi.ArgumentList.Add("--heads");
        // `--` separator so the URL can never be parsed as an option.
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(repositoryUrl);
        psi.ArgumentList.Add("refs/heads/release/*");
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        return psi;
    }

    internal static async Task<(string Host, int Port, string Address)?> ResolveSafeEndpointAsync(
        string repositoryUrl,
        CancellationToken ct)
    {
        var uri = new Uri(repositoryUrl, UriKind.Absolute);
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }

        // Reject mixed answers too: allowing one public answer while a private answer is present lets
        // address selection or retries cross the SSRF boundary.
        if (addresses.Length == 0 || addresses.Any(WebhookSsrfGuard.IsForbiddenAddress)) return null;
        var selected = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? addresses[0];
        var address = selected.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{selected}]"
            : selected.ToString();
        return (uri.Host, uri.IsDefaultPort ? 443 : uri.Port, address);
    }

}
