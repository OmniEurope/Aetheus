// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text.Json;

namespace Aetheus.Back.Components.Servers;

/// <summary>Hash agent-reported inventory fields and rewrite changed sections transactionally with
/// their stored fingerprints, while updating security CheckedAt independently. Force a complete
/// rewrite after <see cref="FullRewriteInterval"/> to bound drift from other writers, restored data
/// or changed mappings.</summary>
internal sealed class HeartbeatInventoryFingerprints
{
    internal static readonly TimeSpan FullRewriteInterval = TimeSpan.FromMinutes(10);

    internal const string Services = "services";
    internal const string DockerContainers = "docker-containers";
    internal const string DockerImages = "docker-images";
    internal const string DockerComposeStacks = "docker-compose-stacks";
    internal const string DockerNetworks = "docker-networks";
    internal const string DockerVolumes = "docker-volumes";
    internal const string Apache = "apache";
    internal const string CertbotCertificates = "certbot-certificates";
    internal const string CertbotState = "certbot-state";
    internal const string Mail = "mail";
    internal const string Teamspeak = "teamspeak";
    internal const string Portsentry = "portsentry";
    internal const string Rkhunter = "rkhunter";
    internal const string SecurityUpdates = "security-updates";
    internal const string Firewall = "firewall";

    private readonly Dictionary<string, string> _previous;
    private readonly Dictionary<string, string> _current;
    private readonly DateTime _fullRewriteAt;

    private HeartbeatInventoryFingerprints(
        Dictionary<string, string> previous, Dictionary<string, string> current, DateTime fullRewriteAt)
    {
        _previous = previous;
        _current = current;
        _fullRewriteAt = fullRewriteAt;
    }

    /// <summary>True when every section is rewritten by this beat (first beat, unreadable or expired fingerprints).</summary>
    public bool IsFullRewrite => _previous.Count == 0;

    public static HeartbeatInventoryFingerprints Compute(string? storedJson, ServerHeartbeatDto heartbeat, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        var stored = Read(storedJson);
        var age = stored is null ? TimeSpan.MaxValue : now - stored.FullRewriteAt;
        var trusted = stored is not null && age >= TimeSpan.Zero && age < FullRewriteInterval;
        var current = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Services] = Hash(heartbeat.Services),
            [DockerContainers] = Hash(heartbeat.Docker.Containers),
            [DockerImages] = Hash(heartbeat.Docker.Images),
            [DockerComposeStacks] = Hash(heartbeat.Docker.ComposeStacks),
            [DockerNetworks] = Hash(heartbeat.Docker.Networks),
            [DockerVolumes] = Hash(heartbeat.Docker.Volumes),
            [Apache] = Hash(heartbeat.Apache),
            [CertbotCertificates] = Hash(heartbeat.Certbot.Certificates),
            [CertbotState] = Hash(new
            {
                heartbeat.Certbot.IsInstalled,
                heartbeat.Certbot.RenewalCheckedAt,
                heartbeat.Certbot.RenewalCheckSucceeded,
                heartbeat.Certbot.HelperVersion,
                heartbeat.Certbot.RenewalCheckFailures,
                heartbeat.Certbot.RenewalCheckOutputTail,
                heartbeat.Certbot.AcmeProxyRules
            }),
            [Mail] = Hash(heartbeat.Mail),
            [Teamspeak] = Hash(heartbeat.Teamspeak),
            [Portsentry] = Hash(heartbeat.Portsentry),
            [Rkhunter] = Hash(heartbeat.Rkhunter),
            [SecurityUpdates] = Hash(heartbeat.SecurityUpdates),
            [Firewall] = Hash(heartbeat.Firewall)
        };
        return trusted
            ? new HeartbeatInventoryFingerprints(stored!.Sections, current, stored.FullRewriteAt)
            : new HeartbeatInventoryFingerprints(new Dictionary<string, string>(StringComparer.Ordinal), current, now);
    }

    /// <summary>Whether this beat must rewrite <paramref name="section"/>.</summary>
    public bool HasChanged(string section) =>
        !_previous.TryGetValue(section, out var previous)
        || !string.Equals(previous, _current[section], StringComparison.Ordinal);

    /// <summary>The value to store once every changed section has been staged in the same save.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Stored(_fullRewriteAt, _current));

    private static string Hash<T>(T section) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(section)).AsSpan(0, 16));

    private static Stored? Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Stored>(json) is { Sections: not null } stored ? stored : null;
        }
        catch (JsonException)
        {
            // Unreadable fingerprints only cost one full rewrite, which also replaces them.
            return null;
        }
    }

    private sealed record Stored(DateTime FullRewriteAt, Dictionary<string, string> Sections);
}
