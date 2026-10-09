// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Agent.Core.Operations;

internal sealed class RestrictedScannerEgress : IAsyncDisposable
{
    internal const string ProxyUserName = "aetheus";
    private readonly IScannerProcessRunner _runner;
    private readonly Func<string, TaskLogLevel, Task> _onOutput;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TcpListener _listener;
    private readonly IReadOnlyDictionary<Destination, IReadOnlyList<IPAddress>> _destinations;
    private readonly string _proxyAuthorization;
    private readonly Task _acceptLoop;

    private RestrictedScannerEgress(
        string networkName,
        IScannerProcessRunner runner,
        Func<string, TaskLogLevel, Task> onOutput,
        TcpListener listener,
        IReadOnlyDictionary<Destination, IReadOnlyList<IPAddress>> destinations,
        string proxySecret)
    {
        NetworkName = networkName;
        _runner = runner;
        _onOutput = onOutput;
        _listener = listener;
        _destinations = destinations;
        ProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        ProxyHost = ((IPEndPoint)listener.LocalEndpoint).Address.ToString();
        ProxyPassword = proxySecret;
        ProxyUrl = $"http://{ProxyUserName}:{proxySecret}@{FormatHost(ProxyHost)}:{ProxyPort}";
        _proxyAuthorization = "Basic " + Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{ProxyUserName}:{proxySecret}"));
        _acceptLoop = AcceptLoopAsync(_cancellation.Token);
    }

    public string NetworkName { get; }
    public string ProxyHost { get; }
    public int ProxyPort { get; }
    internal string ProxyPassword { get; }
    public string ProxyUrl { get; }

    public static async Task<RestrictedScannerEgress?> StartAsync(
        ScannerManifestEntry scanner,
        IReadOnlyDictionary<string, string> envVars,
        IScannerProcessRunner runner,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (!string.Equals(scanner.Execution, "container", StringComparison.Ordinal)
            || string.Equals(scanner.Network, "none", StringComparison.Ordinal))
            return null;

        var requested = new Dictionary<Destination, bool>();
        foreach (var host in scanner.AllowedDestinations ?? [])
        {
            var normalized = NormalizeHost(host);
            if (normalized is not null) requested[new Destination(normalized, 443)] = false;
        }
        var trustedDastTarget = string.Equals(
                Value(envVars, "AETHEUS_SCANNER_TARGET_TRUSTED"), "true", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Value(envVars, "AETHEUS_SCANNER_TARGET_CLASSIFICATION"), "ephemeral", StringComparison.Ordinal);
        AddUriDestination(
            requested, Value(envVars, "AETHEUS_SCANNER_TARGET_URL"), trustedDastTarget);
        foreach (var specification in ScannerApiSpecifications.Read(envVars))
            AddUriDestination(requested, specification, trustedDastTarget);
        if (requested.Count == 0)
            throw new IOException("Networked scanner has no audited destination allowlist.");

        var destinations = new Dictionary<Destination, IReadOnlyList<IPAddress>>();
        foreach (var item in requested)
            destinations[item.Key] = await ResolveAndValidateAsync(item.Key, item.Value, ct).ConfigureAwait(false);

        var networkName = $"aetheus-scan-net-{Guid.NewGuid():N}";
        var create = Process("docker", "network", "create", "--internal", "--label", "aetheus.scan=true", networkName);
        var result = await runner.RunAsync(create, 30, onOutput, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut)
            throw new IOException("Restricted scanner network creation failed.");
        try
        {
            var gateway = await InspectGatewayAsync(networkName, runner, onOutput, ct).ConfigureAwait(false);
            var listener = new TcpListener(gateway, 0);
            listener.Start(32);
            var proxySecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            return new RestrictedScannerEgress(
                networkName, runner, onOutput, listener, destinations, proxySecret);
        }
        catch
        {
            await runner.RunAsync(
                Process("docker", "network", "rm", networkName), 30, onOutput, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var clientStream = client.GetStream();
            var header = await ReadHeaderAsync(clientStream, ct).ConfigureAwait(false);
            if (header is null) return;
            if (!HasValidProxyAuthorization(header))
            {
                await clientStream.WriteAsync(
                    "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"Aetheus\"\r\nConnection: close\r\n\r\n"u8.ToArray(),
                    ct).ConfigureAwait(false);
                return;
            }

            var firstLineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
            if (firstLineEnd <= 0) return;
            var firstLine = header[..firstLineEnd];
            var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return;

            string host;
            int port;
            var connect = string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase);
            if (connect)
            {
                (host, port) = ParseAuthority(parts[1], 443);
            }
            else if (Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttp)
            {
                host = uri.Host;
                port = uri.IsDefaultPort ? 80 : uri.Port;
                parts[1] = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
                header = string.Join(' ', parts) + header[firstLineEnd..];
            }
            else
            {
                return;
            }

            var destination = new Destination(host.TrimEnd('.').ToLowerInvariant(), port);
            if (!_destinations.TryGetValue(destination, out var addresses) || addresses.Count == 0)
                return;

            using var upstream = new TcpClient(addresses[0].AddressFamily);
            await upstream.ConnectAsync(addresses[0], port, ct).ConfigureAwait(false);
            upstream.NoDelay = true;
            var upstreamStream = upstream.GetStream();
            if (connect)
            {
                await clientStream.WriteAsync(
                    "HTTP/1.1 200 Connection Established\r\nConnection: keep-alive\r\n\r\n"u8.ToArray(),
                    ct).ConfigureAwait(false);
            }
            else
            {
                var sanitizedHeader = RemoveProxyAuthorization(header);
                await upstreamStream.WriteAsync(
                    Encoding.ASCII.GetBytes(sanitizedHeader), ct).ConfigureAwait(false);
            }

            using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var upstreamCopy = clientStream.CopyToAsync(upstreamStream, relayCancellation.Token);
            var downstreamCopy = upstreamStream.CopyToAsync(clientStream, relayCancellation.Token);
            await Task.WhenAny(upstreamCopy, downstreamCopy).ConfigureAwait(false);
            await relayCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private bool HasValidProxyAuthorization(string header)
    {
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "Proxy-Authorization:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(line[prefix.Length..].Trim()),
                    Encoding.ASCII.GetBytes(_proxyAuthorization));
        }
        return false;
    }

    private static string RemoveProxyAuthorization(string header) =>
        string.Join("\r\n", header.Split("\r\n")
            .Where(line => !line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)));

    private static async Task<IPAddress> InspectGatewayAsync(
        string networkName,
        IScannerProcessRunner runner,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var lines = new List<string>();
        async Task Capture(string line, TaskLogLevel level)
        {
            lines.Add(line.Trim());
            await onOutput(line, level).ConfigureAwait(false);
        }
        var inspect = Process(
            "docker", "network", "inspect", "--format", "{{(index .IPAM.Config 0).Gateway}}", networkName);
        var result = await runner.RunAsync(inspect, 30, Capture, ct).ConfigureAwait(false);
        var gateway = lines.Select(line => IPAddress.TryParse(line, out var address) ? address : null)
            .FirstOrDefault(address => address is not null);
        return result.ExitCode == 0 && !result.TimedOut && gateway is not null
            ? gateway
            : throw new IOException("Restricted scanner network gateway inspection failed.");
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAndValidateAsync(
        Destination destination,
        bool allowPrivate,
        CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(destination.Host, out var literal))
            addresses = [literal];
        else
            addresses = await Dns.GetHostAddressesAsync(destination.Host, ct).ConfigureAwait(false);
        var validated = addresses
            .Where(address => allowPrivate || !IsPrivateOrSpecial(address))
            .Distinct()
            .ToList();
        if (validated.Count == 0)
            throw new IOException(
                $"Scanner destination '{destination.Host}:{destination.Port}' resolves only to a private or special address.");
        return validated;
    }

    private static bool IsPrivateOrSpecial(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xfe) == 0xfc;
        }
        var octets = address.GetAddressBytes();
        return octets[0] == 0
            || octets[0] == 10
            || octets[0] == 127
            || octets[0] == 169 && octets[1] == 254
            || octets[0] == 172 && octets[1] is >= 16 and <= 31
            || octets[0] == 192 && octets[1] == 168
            || octets[0] >= 224;
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(1024);
        var buffer = new byte[1];
        while (bytes.Count < 32_768)
        {
            if (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) == 0) return null;
            bytes.Add(buffer[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n'
                && bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        return null;
    }

    private static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
    {
        if (Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var uri))
            return (uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port);
        return (string.Empty, defaultPort);
    }

    private static void AddUriDestination(
        IDictionary<Destination, bool> destinations,
        string? value,
        bool allowPrivate)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return;
        var port = uri.IsDefaultPort
            ? uri.Scheme == Uri.UriSchemeHttp ? 80 : 443
            : uri.Port;
        destinations[new Destination(uri.Host.TrimEnd('.').ToLowerInvariant(), port)] = allowPrivate;
    }

    private static string? NormalizeHost(string value) =>
        Uri.CheckHostName(value.TrimEnd('.')) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? value.TrimEnd('.').ToLowerInvariant() : null;

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

    private static string? Value(IReadOnlyDictionary<string, string> env, string key) =>
        env.TryGetValue(key, out var value) ? value : null;

    private static System.Diagnostics.ProcessStartInfo Process(string executable, params string[] arguments)
    {
        var process = new System.Diagnostics.ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) process.ArgumentList.Add(argument);
        return process;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        var removed = await _runner.RunAsync(Process("docker", "network", "rm", NetworkName), 30,
            _onOutput, CancellationToken.None).ConfigureAwait(false);
        if (removed.ExitCode != 0 || removed.TimedOut)
            throw new IOException($"Restricted scanner network cleanup failed for {NetworkName}.");
        _cancellation.Dispose();
    }

    private readonly record struct Destination(string Host, int Port);
}
