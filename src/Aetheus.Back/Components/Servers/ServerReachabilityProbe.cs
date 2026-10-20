// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// External probe that tells a stopped agent apart from a stopped host (PLAN-010 lot 1). When an
/// agent stops heartbeating, the backend opens a TCP connection to the server's stored address on
/// the configured ports: an accepted or refused connection proves the host is up (only the agent
/// stopped); silence on every port, after every retry, means the host is unreachable. It never logs
/// in and never sends a byte. A server with no usable address, an address the outbound policy
/// refuses, or a disabled probe is recorded as unknown, never as reachable.
/// </summary>
internal sealed class ServerReachabilityProbe(
    ITcpReachabilityProber prober,
    IServerReachabilityRepository repository,
    IOptions<ServerReachabilityProbeOptions> options,
    TimeProvider timeProvider,
    ILogger<ServerReachabilityProbe> logger)
{
    /// <summary>Probes the given servers in parallel, then records each result (one at a time: the
    /// repository shares the scope's DbContext). Returns the classification per server id.</summary>
    public async Task<IReadOnlyDictionary<int, ServerReachability>> ProbeAndRecordAsync(
        IReadOnlyList<Server> servers, CancellationToken ct)
    {
        var results = await Task.WhenAll(servers.Select(server => ClassifyAsync(server, ct))).ConfigureAwait(false);
        var probedAt = timeProvider.GetUtcNow().UtcDateTime;
        var byServer = new Dictionary<int, ServerReachability>(servers.Count);
        for (var i = 0; i < servers.Count; i++)
        {
            var (reachability, target) = results[i];
            await repository.RecordAsync(servers[i].Id, reachability, target, probedAt, ct).ConfigureAwait(false);
            byServer[servers[i].Id] = reachability;
            logger.LogInformation(
                "External probe of server {ServerName} (#{ServerId}): {Reachability} ({Target})",
                servers[i].Name, servers[i].Id, reachability, target ?? "no target");
        }
        return byServer;
    }

    /// <summary>Probes again the offline servers whose last probe is older than the recheck interval
    /// or than their last heartbeat, so the displayed state follows the host.</summary>
    public async Task RecheckDueAsync(CancellationToken ct)
    {
        var probedBefore = timeProvider.GetUtcNow().UtcDateTime - options.Value.RecheckInterval;
        var due = await repository.GetOfflineServersDueForProbeAsync(probedBefore, ct).ConfigureAwait(false);
        if (due.Count > 0) await ProbeAndRecordAsync(due, ct).ConfigureAwait(false);
    }

    internal async Task<(ServerReachability Reachability, string? Target)> ClassifyAsync(Server server, CancellationToken ct)
    {
        var settings = options.Value;
        var ports = settings.EffectivePorts;
        if (!settings.Enabled || ports.Length == 0) return (ServerReachability.UnknownProbeDisabled, null);

        var addresses = await ResolveTargetsAsync(server, ct).ConfigureAwait(false);
        if (addresses.Count == 0) return (ServerReachability.UnknownNoAddress, null);

        var allowed = settings.AllowPrivateTargets
            ? addresses
            : [.. addresses.Where(address => !WebhookSsrfGuard.IsForbiddenAddress(address))];
        if (allowed.Count == 0) return (ServerReachability.UnknownAddressNotAllowed, FormatTarget(addresses[0], ports));

        var address = allowed[0];
        var timeout = settings.EffectiveConnectTimeout;
        var backoff = settings.InitialBackoff;
        var attempts = settings.EffectiveAttempts;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var outcomes = await Task.WhenAll(ports.Select(port => prober.ConnectAsync(address, port, timeout, ct)))
                .ConfigureAwait(false);
            var answered = Array.FindIndex(outcomes, outcome => outcome != TcpProbeOutcome.NoAnswer);
            if (answered >= 0) return (ServerReachability.HostReachable, FormatTarget(address, [ports[answered]]));

            if (attempt < attempts && backoff > TimeSpan.Zero)
            {
                await Task.Delay(backoff, timeProvider, ct).ConfigureAwait(false);
                backoff *= 2;
            }
        }
        return (ServerReachability.HostUnreachable, FormatTarget(address, ports));
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveTargetsAsync(Server server, CancellationToken ct)
    {
        if (IPAddress.TryParse(server.IpAddress, out var ip) && !IsUnspecified(ip)) return [ip];
        if (string.IsNullOrWhiteSpace(server.Hostname)) return [];
        var resolved = await prober.ResolveAsync(server.Hostname.Trim(), ct).ConfigureAwait(false);
        return [.. resolved.Where(address => !IsUnspecified(address))];
    }

    private static bool IsUnspecified(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None);

    private static string FormatTarget(IPAddress address, IReadOnlyList<int> ports)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"{host}:{string.Join(",", ports)}";
    }
}
