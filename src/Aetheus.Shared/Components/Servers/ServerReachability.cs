// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Servers;

/// <summary>
/// What the backend's external probe found when a server's agent stopped sending heartbeats
/// (PLAN-010 lot 1). The probe is a plain TCP connect from the backend host to the server's
/// stored address: it never logs in and never sends a byte. It tells a stopped agent on a host
/// that still answers apart from a host that does not answer at all; every other value says why
/// no such answer is available, so a missing probe never reads as a healthy one.
/// </summary>
public enum ServerReachability
{
    /// <summary>No probe has run for the current offline period.</summary>
    NotProbed = 0,

    /// <summary>The host answered on a probed TCP port (accepted or refused the connection):
    /// the machine is up and only the agent stopped.</summary>
    HostReachable = 1,

    /// <summary>No probed port answered within the timeouts, after every retry: the host is
    /// down or cut off from the backend.</summary>
    HostUnreachable = 2,

    /// <summary>Unknown: the server has no usable address (none stored, or the hostname does not
    /// resolve).</summary>
    UnknownNoAddress = 3,

    /// <summary>Unknown: the probe is disabled in the backend configuration.</summary>
    UnknownProbeDisabled = 4,

    /// <summary>Unknown: the address is private, loopback or reserved, and the backend's outbound
    /// policy does not allow probing such targets.</summary>
    UnknownAddressNotAllowed = 5
}
