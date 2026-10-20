// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Servers;

/// <summary>
/// S-FEAT-W8KN: the closed allow-list of OS packages Aetheus may install/uninstall on a managed
/// server. Single source of truth shared by the backend (request validation + service→package
/// resolution), the agent (last-line defence before <c>sudo apt-get</c>) and
/// <see cref="Aetheus.Shared.Components.Shared.OperationTargetValidator"/>.
///
/// The UI/detection layer speaks <b>service keys</b> (systemd unit / well-known service name, e.g.
/// <c>docker</c>, <c>mysql</c>, <c>mongod</c>); apt installs <b>package names</b>, which differ for
/// several services (<c>docker</c> → <c>docker.io</c>, <c>mysql</c> → <c>mysql-server</c>,
/// <c>mariadb</c> → <c>mariadb-server</c>, <c>mongod</c> → <c>mongodb-org</c>). <see cref="All"/>
/// holds the resolved apt names - what the agent runs and what the argv-exact
/// <c>/etc/sudoers.d/aetheus-package</c> drop-in allow-lists (kept in lockstep by
/// <c>SudoersAuditTests</c>).
/// </summary>
public static class ManageablePackages
{
    // service key (UI / detection vocabulary) -> real apt package name.
    private static readonly IReadOnlyDictionary<string, string> ServiceToApt =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nginx"] = "nginx",
            ["apache2"] = "apache2",
            ["docker"] = "docker.io",
            ["fail2ban"] = "fail2ban",
            ["rkhunter"] = "rkhunter",
            ["ufw"] = "ufw",
            ["portsentry"] = "portsentry",
            ["postfix"] = "postfix",
            // Ubuntu has no bare `dovecot` metapackage - the installable package is `dovecot-core`
            // (the service key the agent detects is still "dovecot").
            ["dovecot"] = "dovecot-core",
            // certbot ships as a real apt package on Ubuntu 24.04 (universe). Only `sudo certbot`
            // (a GTFOBins primitive) is forbidden - installing the package via apt is fine, and the
            // Certbot module then reads /etc/letsencrypt/live read-only. So it is installable here.
            ["certbot"] = "certbot",
            ["mysql"] = "mysql-server",
            ["mariadb"] = "mariadb-server",
            ["postgresql"] = "postgresql",
            ["redis-server"] = "redis-server",
            ["mongod"] = "mongodb-org",
        };

    // apt packages that must NOT be uninstalled: removing them loses data (databases), drops the
    // security posture (ufw/fail2ban) or breaks the pipeline-runner (docker). install ⊋ remove.
    private static readonly IReadOnlySet<string> NonRemovable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "docker.io",
        "postgresql",
        "mysql-server",
        "mariadb-server",
        "redis-server",
        "mongodb-org",
        "ufw",
        "fail2ban",
    };

    /// <summary>The UI/detection service keys that can be installed/uninstalled.</summary>
    public static IReadOnlyCollection<string> Services { get; } = [.. ServiceToApt.Keys];

    /// <summary>Every real apt package name the agent may install (and the sudoers install allow-list).</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(ServiceToApt.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>The apt packages that may be uninstalled - <see cref="All"/> minus the protected set.</summary>
    public static IReadOnlySet<string> Removable { get; } =
        new HashSet<string>(
            ((IEnumerable<string>)All).Where(p => !NonRemovable.Contains(p)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a UI service key to its apt package name; null when the key is unknown.</summary>
    public static string? AptPackageFor(string? service) =>
        !string.IsNullOrWhiteSpace(service) && ServiceToApt.TryGetValue(service, out var pkg) ? pkg : null;

    /// <summary>True when <paramref name="service"/> is a known manageable service key.</summary>
    public static bool IsManageableService(string? service) =>
        !string.IsNullOrWhiteSpace(service) && ServiceToApt.ContainsKey(service);

    /// <summary>True when <paramref name="package"/> is a real apt package the agent may install.</summary>
    public static bool IsManageablePackage(string? package) =>
        !string.IsNullOrWhiteSpace(package) && All.Contains(package);

    /// <summary>True when <paramref name="package"/> may be uninstalled (i.e. not a protected package).</summary>
    public static bool IsRemovablePackage(string? package) =>
        !string.IsNullOrWhiteSpace(package) && Removable.Contains(package);
}
