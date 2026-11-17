// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Aetheus.Back.Configuration;

/// <summary>
/// Builds the forwarded-headers options: X-Forwarded-For and X-Forwarded-Proto are honoured only when
/// the socket peer is loopback or inside one of the declared proxy networks (R-52). Every per-address
/// rate limiter reads the address this middleware restores, so an undeclared source must never choose it.
/// </summary>
internal static class ForwardedHeadersConfiguration
{
    /// <summary>Configuration key holding the trusted proxy networks in CIDR notation
    /// (environment form: <c>ForwardedHeaders__KnownNetworks__0=172.16.0.0/12</c>).</summary>
    internal const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";

    /// <summary>
    /// The Docker private range. Production runs the backend in a container behind Apache on the host;
    /// the published 127.0.0.1 port reaches the container from the gateway of its Compose network,
    /// never from loopback. Docker's stock address pools (docker0 172.17.0.0/16, then user-defined
    /// bridges in 172.18-172.31) are all inside 172.16.0.0/12; a host with custom default-address-pools
    /// (the production host allocates 10.0.x.0/24, peer ::ffff:10.0.13.1) is covered by
    /// <see cref="ContainerNetworks"/>.
    /// </summary>
    internal static readonly IReadOnlyList<string> DefaultProxyNetworks = ["172.16.0.0/12"];

    private static readonly IPNetwork[] LoopbackNetworks =
        [new(IPAddress.Loopback, 8), new(IPAddress.IPv6Loopback, 128)];

    /// <summary>Loopback plus the configured networks; when none is configured, the Docker default and
    /// the networks the container itself is attached to. Throws on an entry that is not a valid CIDR network.</summary>
    public static ForwardedHeadersOptions CreateOptions(IConfiguration configuration)
        => CreateOptions(configuration, ContainerNetworks);

    internal static ForwardedHeadersOptions CreateOptions(IConfiguration configuration, Func<IEnumerable<IPNetwork>> containerNetworks)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(containerNetworks);
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in LoopbackNetworks.Concat(ResolveProxyNetworks(configuration, containerNetworks)))
            options.KnownIPNetworks.Add(network);
        return options;
    }

    /// <summary>
    /// The IPv4 subnets of the container's own up interfaces. Inside a container the Compose network's
    /// gateway, which relays the published 127.0.0.1 port, is on the subnet of the container's interface,
    /// whatever pool Docker took it from. Outside a container (a developer machine on a LAN) nothing is
    /// added, so a LAN peer is never trusted by default. The official .NET images set
    /// <c>DOTNET_RUNNING_IN_CONTAINER=true</c>.
    /// </summary>
    internal static IEnumerable<IPNetwork> ContainerNetworks()
        => string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            ? InterfaceNetworks()
            : [];

    /// <summary>The IPv4 subnets of this machine's up, non-loopback interfaces.</summary>
    internal static List<IPNetwork> InterfaceNetworks()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.PrefixLength is > 0 and < 32)
            .Select(unicast => SubnetOf(unicast.Address, unicast.PrefixLength))
            .Distinct()
            .ToList();

    /// <summary>The network of an IPv4 address: host bits cleared, as IPNetwork requires.</summary>
    internal static IPNetwork SubnetOf(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = prefixLength; bit < 32; bit++)
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPNetwork(new IPAddress(bytes), prefixLength);
    }

    private static List<IPNetwork> ResolveProxyNetworks(IConfiguration configuration, Func<IEnumerable<IPNetwork>> containerNetworks)
    {
        var configured = configuration.GetSection(KnownNetworksKey).Get<string[]>();
        if (configured is { Length: > 0 }) return configured.Select(ParseNetwork).ToList();
        return DefaultProxyNetworks.Select(ParseNetwork).Concat(containerNetworks()).ToList();
    }

    private static IPNetwork ParseNetwork(string entry)
    {
        if (!IPNetwork.TryParse(entry?.Trim(), out var network))
            throw new InvalidOperationException(
                $"{KnownNetworksKey} contains '{entry}', which is not a CIDR network such as 172.16.0.0/12.");
        return network;
    }
}
