// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Agents;

/// <summary>
/// The only privilege grants the web UI may ask an agent update to add (R3-58, ADR-051). Every other
/// elevation stays reserved to the installer's <c>--enable-*</c> flags run on the host. The list is a
/// closed whitelist shared by the backend (which refuses anything else), the agent (which refuses it
/// again, whatever the backend sent) and the Linux supervisor (which maps each token to a fixed
/// installer flag). Adding an entry is a security decision recorded in an ADR, never a refactor.
/// </summary>
public static class AgentEnableableCapabilities
{
    /// <summary>The <c>aetheus-patch</c> sudoers drop-in: <c>apt-get upgrade</c>, argv-exact.</summary>
    public const string PatchManage = "patch-manage";

    /// <summary>The <c>aetheus-package</c> drop-in: package install and removal.</summary>
    public const string PackageManage = "package-manage";

    /// <summary>The <c>aetheus-service-enable</c> drop-in: enable or disable a unit at boot.</summary>
    public const string ServiceEnable = "service-enable";

    /// <summary>The <c>aetheus-cron</c> drop-in: runs a job as another local user, never root.</summary>
    public const string CronManage = "cron-manage";

    /// <summary>The rkhunter scan and database update grant; it reports no heartbeat capability.</summary>
    public const string RkhunterManage = "rkhunter-manage";

    /// <summary>The <c>aetheus-apache</c> drop-in (implies Apache introspection).</summary>
    public const string ApacheManage = "apache-manage";

    /// <summary>The <c>aetheus-certbot</c> drop-in (implies Apache management).</summary>
    public const string CertbotManage = "certbot-manage";

    /// <summary>The <c>aetheus-portsentry</c> drop-in.</summary>
    public const string PortsentryManage = "portsentry-manage";

    /// <summary>The <c>aetheus-firewall</c> drop-in.</summary>
    public const string FirewallManage = "firewall-manage";

    /// <summary>The <c>aetheus-mail</c> drop-in: mail stack setup.</summary>
    public const string MailSetup = "mail-setup";

    /// <summary>The TeamSpeak setup grant (implies TeamSpeak management).</summary>
    public const string TeamspeakSetup = "teamspeak-setup";

    /// <summary>Task environment variable carrying the comma-separated capability tokens to the agent.</summary>
    public const string EnvironmentVariable = "AETHEUS_AGENT_ENABLE_CAPABILITIES";

    /// <summary>The closed whitelist, in a stable order.</summary>
    public static IReadOnlyList<string> Whitelist { get; } =
    [
        PatchManage, PackageManage, ServiceEnable, CronManage, RkhunterManage, ApacheManage,
        CertbotManage, PortsentryManage, FirewallManage, MailSetup, TeamspeakSetup
    ];

    /// <summary>
    /// Validates a requested list: each token must equal a whitelist entry exactly (no trimming, no case
    /// folding, no prefix match). On success <paramref name="normalized"/> holds the distinct tokens in
    /// whitelist order; on failure it is empty and <paramref name="rejected"/> names the first offender.
    /// An empty or null request is valid and normalizes to nothing.
    /// </summary>
    public static bool TryNormalize(
        IEnumerable<string?>? requested,
        out IReadOnlyList<string> normalized,
        out string? rejected)
    {
        normalized = [];
        rejected = null;
        if (requested is null) return true;

        var accepted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in requested)
        {
            if (token is null || !Whitelist.Contains(token, StringComparer.Ordinal))
            {
                rejected = token ?? string.Empty;
                return false;
            }
            accepted.Add(token);
        }

        normalized = [.. Whitelist.Where(accepted.Contains)];
        return true;
    }

    /// <summary>Serializes normalized tokens for the task environment and the supervisor request file.</summary>
    public static string Join(IEnumerable<string> normalized) => string.Join(',', normalized);

    /// <summary>Parses the comma-separated wire form with the same strict rules as <see cref="TryNormalize"/>.
    /// Null or empty means none requested.</summary>
    public static bool TryParse(string? value, out IReadOnlyList<string> normalized, out string? rejected) =>
        string.IsNullOrEmpty(value)
            ? TryNormalize(null, out normalized, out rejected)
            : TryNormalize(value.Split(','), out normalized, out rejected);
}
