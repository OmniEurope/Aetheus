// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Shared.Components.Projects;

/// <summary>
/// Recette R-97 (ADR-058): the strict names a project deletion may act on, checked by the backend when it
/// reads the project's libraries and again by the agent before it touches anything (the task is not a trust
/// boundary). Anything that does not match is refused, never escaped: every pattern ends with <c>\z</c> so a
/// trailing line feed cannot slip through, and none admits a space, a quote, a semicolon, a slash where a
/// name is expected or a <c>..</c> segment.
/// </summary>
public static partial class ProjectServerResourceNames
{
    /// <summary>Where the state directory of a project created before R-97 may live: a direct child of this
    /// directory (<c>/var/lib/&lt;slug&gt;-production</c>). Still accepted for those projects; the agent cannot
    /// create nor delete it there, <c>/var/lib</c> belongs to root.</summary>
    public const string StateDirectoryBase = "/var/lib";

    /// <summary>The agent's own working directory (<c>WORK_DIR</c> of the agent installer), owned by its user.</summary>
    public const string AgentWorkDirectory = "/var/lib/aetheus-agent";

    /// <summary>Where a project's state directory lives (R-97): a direct child of this directory, inside the
    /// directory the agent owns, so the agent creates and deletes it without any other right. Nothing else under
    /// <see cref="AgentWorkDirectory"/> is ever a state directory.</summary>
    public const string ProjectStateDirectoryBase = AgentWorkDirectory + "/projects";

    /// <summary>The state directory the pipeline setup wizard writes for an environment of a project:
    /// <c>/var/lib/aetheus-agent/projects/&lt;slug&gt;-&lt;environment&gt;</c>.</summary>
    public static string ProjectStateDirectory(string slug, string environment) =>
        $"{ProjectStateDirectoryBase}/{slug}-{environment}";

    /// <summary>Children of <see cref="StateDirectoryBase"/> that belong to the system, to a service Aetheus or
    /// the host manages (Docker, Apache, certbot, the mail stack, databases, monitoring, security tools) or to
    /// the agent, and that no project may claim even when its slug has the same name (a project called
    /// "docker" or "redis" still never deletes <c>/var/lib/docker</c> or <c>/var/lib/redis</c>). The slug binding
    /// already confines a project to its own name; this list covers the case where that name is a service's.</summary>
    private static readonly HashSet<string> s_reservedStateDirectories = new(StringComparer.Ordinal)
    {
        // The system and its package managers.
        "apt", "aptitude", "cloud", "dbus", "dhcp", "dkms", "dpkg", "fwupd", "grub", "initramfs-tools", "landscape",
        "logrotate", "man-db", "misc", "os-prober", "pam", "plymouth", "polkit-1", "private", "python", "snapd", "sudo",
        "systemd", "ucf", "udisks2", "update-manager", "update-notifier", "upower", "ubuntu-advantage", "usbutils",
        "rsyslog", "chrony", "ntp", "ntpsec", "sss", "tpm", "vim", "locales", "shells.state",
        // Remote access and security tools (the agent manages ssh, fail2ban, rkhunter, portsentry, clamav).
        "ssh", "sshd", "openssh", "fail2ban", "rkhunter", "portsentry", "clamav", "aide", "ufw", "nftables", "iptables",
        // Containers and virtualization.
        "containerd", "docker", "docker-engine", "buildkit", "lxc", "lxcfs", "lxd", "libvirt", "kubelet", "etcd", "cni",
        // Web, TLS, mail.
        "apache2", "nginx", "php", "letsencrypt", "certbot", "postfix", "dovecot", "opendkim", "opendmarc", "spamassassin",
        "amavis", "rspamd", "roundcube", "mailman",
        // Databases, caches, queues, monitoring.
        "mysql", "mysql-files", "mysql-keyring", "mariadb", "postgresql", "redis", "redis-server", "memcached", "mongodb",
        "elasticsearch", "opensearch", "rabbitmq", "influxdb", "prometheus", "grafana", "loki", "node_exporter",
        // Aetheus itself, and other services sometimes hosted beside a project.
        "aetheus", "aetheus-agent", "teamspeak", "git", "gitlab", "jenkins", "samba", "nfs",
    };

    /// <summary>The namespace of the Aetheus control plane: its Compose projects (<c>aetheus-prod</c>), containers,
    /// volumes, networks, state directories and hosts (<c>aetheus-api.example.com</c>) are named <c>aetheus</c> or
    /// <c>aetheus-...</c>.</summary>
    public const string ControlPlaneNamespace = "aetheus";

    /// <summary>Whether <paramref name="name"/> (a Compose project, Docker object, directory leaf or the first label
    /// of a host) is <c>aetheus</c> or starts with <c>aetheus-</c>, <c>aetheus_</c> or <c>aetheus.</c>, whatever its
    /// casing. No project deletion acts on it, even for a project whose slug is <c>aetheus</c>: it could be the
    /// control plane itself (<c>aetheus-prod</c>). <c>aetheusx</c> is another name.</summary>
    public static bool IsReservedForControlPlane(string? name)
    {
        if (name is null || !name.StartsWith(ControlPlaneNamespace, StringComparison.OrdinalIgnoreCase)) return false;
        return name.Length == ControlPlaneNamespace.Length || name[ControlPlaneNamespace.Length] is '-' or '_' or '.';
    }

    /// <summary>Whether the first label of <paramref name="host"/> is in the control plane namespace
    /// (<c>aetheus.example.com</c>, <c>aetheus-api.example.com</c>).</summary>
    public static bool IsControlPlaneNamespaceHost(string? host) =>
        host is not null && IsReservedForControlPlane(host.Split('.')[0]);

    /// <summary>A host a project deletion may act on: a strict host name outside the control plane namespace.
    /// The backend also refuses the control plane's configured public hosts, and the agent the host it reports
    /// to.</summary>
    public static bool IsProjectHost(string? value) => IsHost(value) && !IsControlPlaneNamespaceHost(value);

    public static bool IsComposeProject(string? value) => value is not null && ComposeProject().IsMatch(value);

    /// <summary>A slug as <c>ProjectResourceNames.Slug</c> writes it: lower-case letters and digits, single
    /// hyphens inside, at most 90 characters.</summary>
    public static bool IsProjectSlug(string? value) => value is not null && ProjectSlug().IsMatch(value);

    /// <summary>Whether <paramref name="value"/> is named after the project: <c>&lt;slug&gt;</c>,
    /// <c>&lt;slug&gt;-...</c> or <c>&lt;slug&gt;_...</c> (the wizard writes <c>&lt;slug&gt;-prod</c>).</summary>
    public static bool IsNamedAfter(string value, string slug) =>
        value == slug || value.StartsWith(slug + "-", StringComparison.Ordinal) || value.StartsWith(slug + "_", StringComparison.Ordinal);

    /// <summary>A Compose project of the project: a valid name, named after its slug, outside the control plane
    /// namespace.</summary>
    public static bool IsComposeProjectOf(string? value, string? slug) =>
        IsComposeProject(value) && !IsReservedForControlPlane(value) && IsProjectSlug(slug) && IsNamedAfter(value!, slug!);

    /// <summary>A lower-case DNS host name with at least two labels; no wildcard, no trailing dot.</summary>
    public static bool IsHost(string? value) => value is not null && Host().IsMatch(value);

    /// <summary>A container, volume or network name as Docker prints it.</summary>
    public static bool IsDockerObjectName(string? value) => value is not null && DockerObjectName().IsMatch(value);

    /// <summary>The virtual host files a deployment writes for a host (ADR-057): HTTP then HTTPS.</summary>
    public static IReadOnlyList<string> VirtualHostFiles(string host) => [$"{host}.conf", $"{host}-ssl.conf"];

    /// <summary>
    /// An absolute path that is a direct child of <see cref="ProjectStateDirectoryBase"/> or, for a project created
    /// before R-97, of <see cref="StateDirectoryBase"/>: lower-case letters, digits, dots, underscores and hyphens
    /// only, never <c>..</c>, never a reserved system directory, never the control plane's. This is the confinement
    /// the agent checks again before it deletes anything.
    /// </summary>
    public static bool IsStateDirectory(string? path) =>
        IsWellFormedStateDirectory(path) && !IsReservedForControlPlane(StateDirectoryLeaf(path!));

    /// <summary><see cref="IsStateDirectory"/> before the control plane namespace is applied: a strict direct child of
    /// <see cref="ProjectStateDirectoryBase"/>, or a strict direct child of <see cref="StateDirectoryBase"/> that is no
    /// system or service directory. Anything else under <see cref="AgentWorkDirectory"/> (its <c>w</c>, <c>keys</c>,
    /// <c>deploy</c>...) is refused, as is a deeper path. Lets the backend say why a value is refused (an invalid
    /// path, or one that may be the control plane's).</summary>
    public static bool IsWellFormedStateDirectory(string? path)
    {
        if (path is null) return false;
        if (ProjectStateDirectoryPath().IsMatch(path))
            return IsStrictLeaf(StateDirectoryLeaf(path));
        if (!StateDirectory().IsMatch(path)) return false;
        var leaf = StateDirectoryLeaf(path);
        return IsStrictLeaf(leaf)
            && !s_reservedStateDirectories.Contains(leaf)
            && !leaf.StartsWith("aetheus-agent", StringComparison.Ordinal);
    }

    /// <summary>A state directory of the project: its last segment is <c>&lt;slug&gt;</c> or <c>&lt;slug&gt;-...</c>
    /// (the wizard writes <c>/var/lib/aetheus-agent/projects/&lt;slug&gt;-production</c>; projects created before
    /// R-97 hold <c>/var/lib/&lt;slug&gt;-production</c>).</summary>
    public static bool IsStateDirectoryOf(string? path, string? slug)
    {
        if (!IsStateDirectory(path) || !IsProjectSlug(slug)) return false;
        var leaf = StateDirectoryLeaf(path!);
        return leaf == slug || leaf.StartsWith(slug + "-", StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="path"/> has the shape of a state directory inside
    /// <see cref="ProjectStateDirectoryBase"/>: then the agent also refuses it when that directory or
    /// <see cref="AgentWorkDirectory"/> is a symbolic link.</summary>
    public static bool IsInProjectStateDirectoryBase(string? path) => path is not null && ProjectStateDirectoryPath().IsMatch(path);

    /// <summary>Where the generic deploy templates unpack the repository copy of a deployment (<c>${HOME}/.deployments</c>,
    /// the agent's <c>HOME</c> being its working directory): a direct child of this directory per Compose project.</summary>
    public const string DeploymentsBase = AgentWorkDirectory + "/.deployments";

    /// <summary>The directory a deployment of the Compose project unpacks the repository in: exactly
    /// <c>DEPLOY_DIR=${HOME}/.deployments/$PROJECT</c> of the templates, <c>$PROJECT</c> being <c>COMPOSE_PROJECT</c>.</summary>
    public static string DeploymentDirectory(string composeProject) => $"{DeploymentsBase}/{composeProject}";

    /// <summary>Whether <paramref name="path"/> is exactly the deployment directory of the Compose project
    /// <paramref name="composeProject"/>, itself a strict name outside the control plane namespace (so no <c>..</c>, no
    /// slash and no dot can reach another directory).</summary>
    public static bool IsDeploymentDirectoryOf(string? path, string? composeProject) =>
        IsComposeProject(composeProject) && !IsReservedForControlPlane(composeProject) && path == DeploymentDirectory(composeProject!);

    /// <summary>Whether <paramref name="path"/> has the shape of a deployment directory: then the agent also refuses it
    /// when <see cref="DeploymentsBase"/> or <see cref="AgentWorkDirectory"/> is a symbolic link.</summary>
    public static bool IsInDeploymentsBase(string? path) =>
        path is not null && path.StartsWith(DeploymentsBase + "/", StringComparison.Ordinal)
        && IsComposeProject(path[(DeploymentsBase.Length + 1)..]);

    /// <summary>The last segment of a path the state directory patterns matched.</summary>
    private static string StateDirectoryLeaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool IsStrictLeaf(string leaf) => !leaf.Contains("..", StringComparison.Ordinal) && !leaf.EndsWith('.');

    /// <summary>The directory where the agent keeps the archives of a backup policy (BackupOperationExecutor).</summary>
    public static string BackupArchiveDirectory(int policyId) =>
        string.Create(CultureInfo.InvariantCulture, $"{BackupConstants.BaseDir}/{policyId}");

    public static bool IsBackupPolicyId(int policyId) => policyId > 0;

    /// <summary>Whether <paramref name="path"/> is exactly the archive directory of a backup policy.</summary>
    public static bool IsBackupArchiveDirectory(string? path)
    {
        if (path is null || !path.StartsWith(BackupConstants.BaseDir + "/", StringComparison.Ordinal)) return false;
        var id = path[(BackupConstants.BaseDir.Length + 1)..];
        return PolicyId().IsMatch(id)
            && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && IsBackupPolicyId(value);
    }

    /// <summary>Where the blue-green upstream file of a project may live: a direct child of this directory.</summary>
    public const string SitesAvailableDirectory = "/etc/apache2/sites-available";

    /// <summary>A strict direct child of <see cref="SitesAvailableDirectory"/>: lower-case letters, digits, dots,
    /// underscores and hyphens, ending in <c>.conf</c>, never <c>..</c>. The setup wizard writes
    /// <c>000-&lt;slug&gt;-upstream.conf</c>.</summary>
    public static bool IsWellFormedUpstreamConfiguration(string? path) =>
        path is not null && UpstreamConfiguration().IsMatch(path) && !UpstreamConfigurationFile(path).Contains("..", StringComparison.Ordinal);

    /// <summary><see cref="IsWellFormedUpstreamConfiguration"/> outside the control plane namespace (the control plane's
    /// own <c>000-aetheus-upstream.conf</c> is never a project's).</summary>
    public static bool IsUpstreamConfiguration(string? path) =>
        IsWellFormedUpstreamConfiguration(path) && !IsReservedForControlPlane(UpstreamConfigurationStem(path!));

    /// <summary>An upstream file of the project: its name, without an ordering prefix of digits (<c>000-</c>) and
    /// <c>.conf</c>, is named after the slug (<c>000-&lt;slug&gt;-upstream.conf</c>), so <c>000-default.conf</c> or
    /// another project's file is never one.</summary>
    public static bool IsUpstreamConfigurationOf(string? path, string? slug) =>
        IsUpstreamConfiguration(path) && IsProjectSlug(slug) && IsNamedAfter(UpstreamConfigurationStem(path!), slug!);

    /// <summary>The file name of an upstream path (<c>000-w9-upstream.conf</c>).</summary>
    public static string UpstreamConfigurationFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path[(path.LastIndexOf('/') + 1)..];
    }

    /// <summary>The file name of an upstream path without an ordering prefix of digits and <c>.conf</c>
    /// (<c>000-w9-upstream.conf</c> gives <c>w9-upstream</c>).</summary>
    public static string UpstreamConfigurationStem(string path)
    {
        var file = UpstreamConfigurationFile(path);
        var stem = file.EndsWith(".conf", StringComparison.Ordinal) ? file[..^".conf".Length] : file;
        return OrderingPrefix().Replace(stem, string.Empty, 1);
    }

    /// <summary>An image as the agent names it: <c>repository:tag</c> (a registry with a port allowed), or the
    /// 12-character short id of an untagged image.</summary>
    public static bool IsImageName(string? value) => value is not null && (ImageReference().IsMatch(value) || ImageShortId().IsMatch(value));

    /// <summary>Whether a resource reference is well formed: the name fits its kind and belongs to its scope
    /// (the Compose project of a container, the host of a virtual host).</summary>
    public static bool IsResource(ProjectServerResourceKind kind, string? name, string? scope) => kind switch
    {
        ProjectServerResourceKind.Container or ProjectServerResourceKind.Volume or ProjectServerResourceKind.Network =>
            IsDockerObjectName(name) && IsComposeResource(name, scope),
        ProjectServerResourceKind.Image => IsImageName(name) && IsComposeResource(name, scope),
        ProjectServerResourceKind.VirtualHost => IsProjectHost(scope) && VirtualHostFiles(scope!).Contains(name),
        ProjectServerResourceKind.DeploymentDirectory => IsDeploymentDirectoryOf(name, scope),
        _ => name == scope && IsScopeNamedResource(kind, scope)
    };

    /// <summary>The kinds whose name is their scope (a certificate is its host, a directory its path).</summary>
    private static bool IsScopeNamedResource(ProjectServerResourceKind kind, string? scope) => kind switch
    {
        ProjectServerResourceKind.UpstreamConfiguration => IsUpstreamConfiguration(scope),
        ProjectServerResourceKind.Certificate => IsProjectHost(scope),
        ProjectServerResourceKind.StateDirectory => IsStateDirectory(scope),
        ProjectServerResourceKind.BackupArchives => IsBackupArchiveDirectory(scope),
        _ => false
    };

    /// <summary>A Docker object of a Compose project: neither it nor its project is named like the control plane.</summary>
    private static bool IsComposeResource(string? name, string? scope) =>
        !IsReservedForControlPlane(name) && IsComposeProject(scope) && !IsReservedForControlPlane(scope);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,62}\z")]
    private static partial Regex ComposeProject();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,88}[a-z0-9])?\z")]
    private static partial Regex ProjectSlug();

    [GeneratedRegex(@"^(?=.{4,253}\z)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\z")]
    private static partial Regex Host();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,254}\z")]
    private static partial Regex DockerObjectName();

    [GeneratedRegex(@"^/var/lib/[a-z0-9][a-z0-9._-]{0,99}\z")]
    private static partial Regex StateDirectory();

    [GeneratedRegex(@"^/var/lib/aetheus-agent/projects/[a-z0-9][a-z0-9._-]{0,99}\z")]
    private static partial Regex ProjectStateDirectoryPath();

    [GeneratedRegex(@"^[1-9][0-9]{0,9}\z")]
    private static partial Regex PolicyId();

    [GeneratedRegex(@"^/etc/apache2/sites-available/[a-z0-9][a-z0-9._-]{0,122}\.conf\z")]
    private static partial Regex UpstreamConfiguration();

    [GeneratedRegex(@"^[0-9]+-")]
    private static partial Regex OrderingPrefix();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/-]{0,200}(?::[0-9]{1,5}/[a-z0-9][a-z0-9._/-]{0,200})?:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z")]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"^[0-9a-f]{12}\z")]
    private static partial Regex ImageShortId();
}
