// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Configuration of the external probe that tells a stopped agent apart from a stopped host
/// (PLAN-010 lot 1). Bound from the <c>ServerReachabilityProbe</c> section. The probe only opens a
/// TCP connection and closes it: no login, no credential, no payload.
/// </summary>
public sealed class ServerReachabilityProbeOptions
{
    public const string SectionName = "ServerReachabilityProbe";

    /// <summary>Ports tried when <see cref="Ports"/> is empty: SSH, then HTTPS for a web host.</summary>
    internal static readonly int[] DefaultPorts = [22, 443];

    /// <summary>When false, no probe runs and an offline server reads "unknown, probe disabled".</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>TCP ports tried on the server's address; any answer proves the host is up. Empty means
    /// <see cref="DefaultPorts"/> (an array is used so configuration replaces the list instead of
    /// appending to a default).</summary>
    public int[] Ports { get; set; } = [];

    /// <summary>How long one TCP connect may wait before counting as no answer.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Rounds of connects before the host is declared unreachable (clamped to 1-5).</summary>
    public int Attempts { get; set; } = 3;

    /// <summary>Wait before the second round; doubles before each later round.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>An offline server is probed again once its last probe is older than this, so the
    /// displayed state follows a host that comes back without its agent (or goes down later).</summary>
    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Same outbound policy as webhooks, package feeds and service connections: private,
    /// loopback and reserved addresses are refused unless explicitly allowed. A backend on a public VPS
    /// cannot reach a LAN address anyway, so probing one would report a false "unreachable".</summary>
    public bool AllowPrivateTargets { get; set; }

    internal int[] EffectivePorts =>
        [.. (Ports.Length == 0 ? DefaultPorts : Ports).Where(port => port is > 0 and <= 65535).Distinct()];

    internal int EffectiveAttempts => Math.Clamp(Attempts, 1, 5);

    internal TimeSpan EffectiveConnectTimeout =>
        TimeSpan.FromMilliseconds(Math.Clamp(ConnectTimeout.TotalMilliseconds, 200, 10_000));
}
