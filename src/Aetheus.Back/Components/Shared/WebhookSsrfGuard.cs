// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;

namespace Aetheus.Back.Components.Shared;

/// <summary>
/// Shared SSRF guard for webhook outbound traffic. Used both at submission-time validation
/// (WebhookService) and at connect-time (SocketsHttpHandler.ConnectCallback) to defeat
/// DNS-rebinding TOCTOU between the validation and the actual TCP connect.
/// </summary>
public static class WebhookSsrfGuard
{
    public static bool IsForbiddenAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsForbiddenIpv4(address.GetAddressBytes());
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return IsForbiddenIpv6(address);

        return false;
    }

    /// <summary>The forbidden IPv4 ranges as network and mask: private (10/8, 172.16/12, 192.168/16),
    /// link-local (169.254/16), carrier-grade NAT (100.64/10), IETF protocol (192.0.0/24), benchmarking
    /// (198.18/15), "this network" (0/8), and multicast plus reserved (224/3).</summary>
    private static readonly (uint Network, uint Mask)[] ForbiddenIpv4Ranges =
    [
        Range(10, 0, 0, 8), Range(172, 16, 0, 12), Range(192, 168, 0, 16), Range(169, 254, 0, 16),
        Range(100, 64, 0, 10), Range(192, 0, 0, 24), Range(198, 18, 0, 15), Range(0, 0, 0, 8), Range(224, 0, 0, 3)
    ];

    private static (uint Network, uint Mask) Range(byte a, byte b, byte c, int prefixBits) =>
        (((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8), uint.MaxValue << (32 - prefixBits));

    private static bool IsForbiddenIpv4(byte[] bytes)
    {
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return ForbiddenIpv4Ranges.Any(range => (value & range.Mask) == range.Network);
    }

    private static bool IsForbiddenIpv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return true;
        if (IPAddress.IPv6Any.Equals(address) || IPAddress.IPv6Loopback.Equals(address))
            return true;
        if ((address.GetAddressBytes()[0] & 0xfe) == 0xfc)
            return true;
        return address.IsIPv4MappedToIPv6 && IsForbiddenAddress(address.MapToIPv4());
    }
}
