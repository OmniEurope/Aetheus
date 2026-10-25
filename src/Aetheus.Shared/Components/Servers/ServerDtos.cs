// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aetheus.Shared.Components.Servers;

public record ServerDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;
    public string OsDescription { get; init; } = string.Empty;
    public string AgentVersion { get; init; } = string.Empty;
    public int? AgentProtocolVersion { get; init; }
    public List<string> AgentCapabilities { get; init; } = [];
    public AgentCompatibilityDto? AgentCompatibility { get; init; }
    public bool AgentUpdateReserved { get; init; }
    public AgentUpdateRequestSummaryDto? AgentUpdateRequest { get; init; }
    public ServerStatus Status { get; init; }
    public ServerType Type { get; init; }
    public DateTime LastHeartbeat { get; init; }
    public List<string> Tags { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public int OrganizationId { get; init; }
    /// <summary>UTC instant the agent binary was last (re)installed (the "last update"
    /// timestamp shown on the agent page). Null when the agent hasn't reported it yet -
    /// older agents that predate the marker file simply omit the field.</summary>
    public DateTime? AgentInstalledAt { get; init; }

    /// <summary>PLAN-010 lot 1: what the backend's external TCP probe found the last time this
    /// server's agent stopped heartbeating. Meaningful only while <see cref="Status"/> is Offline.</summary>
    public ServerReachability ExternalProbe { get; init; }

    /// <summary>When the external probe last ran (UTC on the wire); null when never probed.</summary>
    public DateTime? ExternalProbeAt { get; init; }

    /// <summary>The address and port(s) the external probe tried, e.g. <c>203.0.113.5:22</c>;
    /// null when no address was probed.</summary>
    public string? ExternalProbeTarget { get; init; }

    /// <summary>Whether this server is allowed to execute pipeline steps. Off by default on
    /// enrollment (secure-by-default); flipped on by an admin via the server detail UI. When
    /// off, the scheduler never resolves a stage target to this server even if tags/pool match.
    /// S-TECH-CUNK: tri-state on the list/tile - <c>null</c> = UNKNOWN (agent never phoned home yet),
    /// distinct from a reported <c>false</c>.</summary>
    public bool? PipelineRunnerEnabled { get; init; }

    /// <summary>Auto-detected by the agent (<c>docker --version</c> succeeds). Drives whether a
    /// stage requesting <c>isolation: container</c> can run here - a container request on a
    /// Docker-less runner is blocked (fail-closed), never silently downgraded to process mode.</summary>
    public bool DockerAvailable { get; init; }

    /// <summary>S-FEAT-W8KN: the agent has the package-manage controlled-sudo capability, so the
    /// server can install/uninstall managed OS packages. Derived every heartbeat from the reported
    /// sudoers drop-ins; the UI gates the install/uninstall actions on it.
    /// S-TECH-CUNK: tri-state on the list/tile - <c>null</c> = UNKNOWN (agent never phoned home yet).</summary>
    public bool? PackageManagementAvailable { get; init; }

    /// <summary>ADR-024 4.1: the agent has the patch-manage controlled-sudo capability (the argv-exact
    /// <c>aetheus-patch</c> drop-in granting <c>apt-get upgrade</c>), so the fleet can APPLY pending OS
    /// updates. Derived every heartbeat from the reported sudoers drop-ins; the UI gates the Apply action
    /// on it. Pending-update VISIBILITY (the badge) does not require it - the dry-run probe is unprivileged.
    /// S-TECH-CUNK: tri-state - <c>null</c> = UNKNOWN (agent never phoned home yet).</summary>
    public bool? PatchManagementAvailable { get; init; }

    /// <summary>ADR-024 4.2: the agent has the firewall-manage controlled-sudo capability (the root-owned
    /// <c>aetheus-firewall</c> helper grant), so the fleet can mutate ufw rules. Derived every heartbeat from
    /// the reported sudoers drop-ins; the UI gates the open/close/toggle actions on it. Firewall VISIBILITY
    /// (status + rules) does not require it - <c>ufw status</c> is read via a read-only path.
    /// S-TECH-CUNK: tri-state - <c>null</c> = UNKNOWN (agent never phoned home yet).</summary>
    public bool? FirewallManagementAvailable { get; init; }

    /// <summary>S-FEAT-W8KN: the agent has the mail-setup controlled-sudo capability (the root-owned
    /// mail-setup helper grant is present), so the server can run a full mail-server setup. Derived every
    /// heartbeat from the reported sudoers drop-ins; the UI gates the mail Setup action on it.</summary>
    public bool MailSetupAvailable { get; init; }

    /// <summary>The agent has the teamspeak-setup controlled-sudo capability (the root-owned teamspeak-setup
    /// helper grant is present), so the server can install a full TeamSpeak 3 server. Derived every
    /// heartbeat from the reported sudoers drop-ins; the UI gates the teamspeak Install action on it.</summary>
    public bool TeamspeakSetupAvailable { get; init; }

    /// <summary>Cross-agent deploy capability - the deployment module's controlled-sudo helper passed its
    /// versioned functional probe, so this server can be the target of a <c>type: deploy</c> step. Drives
    /// the "Déploiement" badge and targeting gate.
    /// S-TECH-CUNK: tri-state on the list/tile - <c>null</c> = UNKNOWN (agent never phoned home yet).</summary>
    public bool? DeploymentTargetAvailable { get; init; }

    /// <summary>The agent publishes <c>ports.observe</c>, so it can scan the host's listening TCP
    /// ports. Unlike the sudo-derived flags this needs no grant, only an agent recent enough to
    /// carry the collector; the UI gates the "refresh scan" action on it. Tri-state - <c>null</c> =
    /// UNKNOWN (agent never phoned home yet).</summary>
    public bool? PortObservationAvailable { get; init; }

    /// <summary>When the host's listening ports were last observed, null when never scanned. It is a
    /// server-level fact on purpose: a host that listens on nothing produces no reservation row at
    /// all, and "no row" must not read as "never scanned".</summary>
    public DateTime? PortsObservedAt { get; init; }

    /// <summary>Admin policy: when true, this runner refuses any pipeline step that is NOT
    /// container-isolated. Off by default. Lets an operator enforce "containers only" on a
    /// sensitive runner regardless of what a pipeline YAML requests.</summary>
    public bool RequireContainerIsolation { get; init; }

    /// <summary>S-DES-23: the agent runs with <c>AllowInsecureCerts</c> (TLS validation disabled -
    /// dev only). Reported every heartbeat; drives a discreet "TLS non vérifié" badge on the server card.</summary>
    public bool InsecureTls { get; init; }

    /// <summary>S-TECH-CDUI: the agent's most recent capability diagnostics (e.g. a sudoers drop-in present
    /// but unreadable, a sudo probe that failed) - persisted server-side and surfaced in the UI so an
    /// operator can see WHY a capability is OFF without SSHing. Empty when the last heartbeat reported none.</summary>
    public List<string> CapabilityDiagnostics { get; init; } = [];
    public List<string> ScannerCapabilities { get; init; } = [];
    public StorageDiagnosticsDto StorageDiagnostics { get; init; } = new();

    /// <summary>
    /// Non-secret agent configuration, reported so an operator can diagnose a misbehaving agent from
    /// the UI instead of reading files on the host. Deliberately excludes the enrolment token and every
    /// other credential: a heartbeat is stored and logged, so nothing secret may travel here.
    /// Null identifies an agent predating the field.
    /// </summary>
    public AgentConfigurationDto? AgentConfiguration { get; init; }
}

/// <summary>
/// What the agent is actually configured with, as opposed to what the operator believes.
/// Every field here answered a real production incident: a ServerUrl left on http:// broke package
/// publishing, and a posture that no upgrade ever replayed silently revoked certbot.
/// </summary>
public sealed record AgentConfigurationDto
{
    /// <summary>Backend URL the agent calls. Non-secret, and the scheme alone explains a whole class
    /// of failures (plain HTTP makes NuGet refuse a package push).</summary>
    [StringLength(500)]
    public string? ServerUrl { get; init; }

    /// <summary>Directory the agent binaries run from.</summary>
    [StringLength(500)]
    public string? InstallDirectory { get; init; }

    /// <summary>Durable working directory (offline queue, staging, install markers).</summary>
    [StringLength(500)]
    public string? WorkDirectory { get; init; }

    /// <summary>System account the agent process runs as. Explains most permission failures.</summary>
    [StringLength(100)]
    public string? RunAsUser { get; init; }

    /// <summary>Contents of the installer posture stamp. A value behind the installer means the host
    /// never replayed the privileged provisioning, which is invisible from the version alone.</summary>
    [StringLength(50)]
    public string? PostureVersion { get; init; }

    /// <summary>Whether the root-owned posture-upgrade worker is installed. Without it a self-update
    /// swaps binaries but can never re-provision sudoers, ACLs or state directories.</summary>
    public bool? PostureUpgradeWorkerInstalled { get; init; }

    /// <summary>Heartbeat cadence in seconds, as configured.</summary>
    [Range(1, 86400)]
    public int? HeartbeatIntervalSeconds { get; init; }

    /// <summary>Configured minimum log level.</summary>
    [StringLength(20)]
    public string? LogLevel { get; init; }
}

public sealed record ServerDetailDto : ServerDto
{
    public string IpAddress { get; init; } = string.Empty;
    public double CpuPercent { get; init; }
    public double MemoryUsedMb { get; init; }
    public double MemoryTotalMb { get; init; }
    public double DiskUsedGb { get; init; }
    public double DiskTotalGb { get; init; }
    /// <summary>Fullest inode table of the last metric, in percent; null when unknown.</summary>
    public double? InodeUsedPercent { get; init; }
    /// <summary>The distinct filesystems of the last heartbeat, for the storage section; empty until the
    /// agent reported them.</summary>
    [MaxLength(64)]
    public List<DiskInfoDto> Disks { get; init; } = [];
    /// <summary>Agent clock offset against the backend's, in milliseconds (positive = agent ahead),
    /// and its uncertainty; both null when unknown (PLAN-010 lot 1).</summary>
    public double? ClockOffsetMs { get; init; }
    public double? ClockOffsetUncertaintyMs { get; init; }
    [MaxLength(2048)]
    public List<ServiceInfoDto> Services { get; init; } = [];
    public List<ServerTaskDto> RecentTasks { get; init; } = [];
    public DockerDataDto Docker { get; init; } = new();
    public ApacheDataDto Apache { get; init; } = new();
    public CertbotDataDto Certbot { get; init; } = new();
    public CronDataDto Cron { get; init; } = new();
    public MailDataDto Mail { get; init; } = new();
    public TeamspeakDataDto Teamspeak { get; init; } = new();
    public PortsentryDataDto Portsentry { get; init; } = new();
    public RkhunterDataDto Rkhunter { get; init; } = new();
    /// <summary>OOM kills since the host's last boot; null when the agent does not measure them.</summary>
    public long? OomKillsSinceBoot { get; init; }

    /// <summary>PLAN-010 lot 1: the stored SSH snapshot and its recent changes; null until the
    /// agent reported one.</summary>
    public SshConfigStatusDto? SshConfig { get; init; }

    /// <summary>S-TECH-N8R3: recent metric-report timestamps (UTC, oldest→newest) over the last
    /// ~30 minutes. Each entry is a heartbeat the server sent; the UI buckets these into a real
    /// "uptime % (30 min)" and a sparkline instead of inferring liveness from a single LastHeartbeat.</summary>
    public List<DateTime> HeartbeatHistory { get; init; } = [];
}

public sealed record ServerHeartbeatDto
{
    /// <summary>Opaque id of the current agent process lifetime. Changes on every agent restart.</summary>
    [StringLength(64)]
    public string? AgentSessionId { get; init; }
    [StringLength(50)]
    public string? AgentVersion { get; init; }
    /// <summary>Monotone backend-agent contract version. Every supported agent reports it.</summary>
    [Range(1, int.MaxValue)]
    public int? AgentProtocolVersion { get; init; }
    /// <summary>Sorted stable identifiers the binary and host can effectively execute.</summary>
    [MaxLength(128)]
    [MaxItemStringLength(200)]
    public List<string>? AgentCapabilities { get; init; }
    /// <summary>UTC instant of the agent binary's last (re)install. Read once at agent
    /// startup from <c>$WORK_DIR/.installed-at</c>, with assembly mtime as fallback.</summary>
    public DateTime? AgentInstalledAt { get; init; }
    /// <summary>True when the agent host has a working Docker CLI (<c>docker --version</c> exit 0).
    /// Reported every heartbeat so the capability self-heals if Docker is installed/removed.</summary>
    public bool DockerAvailable { get; init; }
    /// <summary>Agent-reported: Git workspace preparation is available. Application SDKs are
    /// resolved in containers and <see cref="DockerAvailable"/> reports the OCI capability. Nullable
    /// so a pre-feature agent (which omits it) reads as null = "unknown" rather than "absent".
    /// Reported every heartbeat; the backend self-heals the runner gate down when it goes false.</summary>
    public bool? PipelineRunnerAvailable { get; init; }
    /// <summary>S-DES-23: agent runs with TLS validation disabled (AllowInsecureCerts, dev only).</summary>
    public bool InsecureTls { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryUsedMb { get; init; }
    public double MemoryTotalMb { get; init; }
    [MaxLength(64)]
    public List<DiskInfoDto> Disks { get; init; } = [];
    [MaxLength(2048)]
    public List<ServiceInfoDto> Services { get; init; } = [];
    public DockerDataDto Docker { get; init; } = new();
    public ApacheDataDto Apache { get; init; } = new();
    public CertbotDataDto Certbot { get; init; } = new();
    public CronDataDto Cron { get; init; } = new();
    public MailDataDto Mail { get; init; } = new();
    public TeamspeakDataDto Teamspeak { get; init; } = new();
    public PortsentryDataDto Portsentry { get; init; } = new();
    public RkhunterDataDto Rkhunter { get; init; } = new();
    public SecurityUpdatesDataDto SecurityUpdates { get; init; } = new();
    public FirewallDataDto Firewall { get; init; } = new();
    /// <summary>OOM kills since boot (Linux); null when not measured, which never alerts.</summary>
    public OomKillReportDto? OomKills { get; init; }

    /// <summary>
    /// S-TECH-15: SHA256 hashes of the agent's sudoers drop-in files
    /// (<c>/etc/sudoers.d/aetheus-*</c>), keyed by file name. Empty when the agent
    /// has no sudoers grants or can't read them. The backend captures a baseline on
    /// first report and raises a Sec-Audit alert if a hash later diverges.
    /// </summary>
    [BoundedDictionary(32, 100, 64)]
    public Dictionary<string, string> SudoersHashes { get; init; } = [];

    /// <summary>
    /// Whether <see cref="SudoersHashes"/> comes from a completed inventory (possibly empty).
    /// Null identifies legacy agents; an empty legacy payload is not authoritative because it also
    /// represented a timeout or unreadable inventory.
    /// </summary>
    public bool? SudoersInventoryAvailable { get; init; }

    /// <summary>PLAN-010 lot 1: the effective sshd settings and the authorized-key fingerprints.
    /// Null for an agent predating the field or before its first collection completed.</summary>
    public SshConfigDataDto? SshConfig { get; init; }

    /// <summary>
    /// S-FEAT-HBDX / S-TECH-CAPX: agent-reported capability diagnostics that explain WHY a derived
    /// capability shows OFF despite the grant being installed - e.g. "sudoers drop-in aetheus-package
    /// present but unreadable" (the agent lacks the read ACL, so the hash never reaches the heartbeat) or
    /// "passwordless sudo unavailable" (a real <c>sudo -n -l</c> probe failed, so the sandbox blocks
    /// elevation even where a grant exists). Empty when nothing is amiss. Lets an operator see the real
    /// cause without SSHing to the box.
    /// </summary>
    [MaxLength(32)]
    [MaxItemStringLength(1000)]
    public List<string> CapabilityDiagnostics { get; init; } = [];
    [MaxLength(32)]
    [MaxItemStringLength(200)]
    public List<string> ScannerCapabilities { get; init; } = [];
    public StorageDiagnosticsDto StorageDiagnostics { get; init; } = new();

    /// <summary> The TCP ports actually listening on the host, refreshed on the slow inventory
    /// period. Paired with <see cref="ObservedPortsAvailable"/> because an empty list is ambiguous
    /// on its own - it also describes an agent that has never had the collector. </summary>
    [MaxLength(PortRegistryLimits.MaxObservedPorts)]
    public List<ObservedPortDto> ObservedPorts { get; init; } = [];

    /// <summary>
    /// Whether <see cref="ObservedPorts"/> comes from a completed scan (possibly empty). Null
    /// identifies an agent predating the collector; treating that as "nothing listens" would erase
    /// every observation the registry holds for the server on the first legacy heartbeat.
    /// </summary>
    public bool? ObservedPortsAvailable { get; init; }

    /// <summary>
    /// The four instants of the agent's previous heartbeat exchange, from which the backend estimates
    /// the agent's clock offset. Null from an agent predating the measure, on the first beat after a
    /// start, or when the backend did not stamp its reply: the offset is then unknown, never zero.
    /// </summary>
    public ClockSampleDto? ClockSample { get; init; }
}

/// <summary>
/// Heartbeat reply. <see cref="RenewedToken"/> is non-null only when the server
/// rolled the agent's Bearer token (it was near expiry). It is plaintext and
/// returned exactly once - the agent must persist and swap it. Empty otherwise,
/// so older agents that ignore the body are unaffected.
/// </summary>
public sealed record ServerHeartbeatResponseDto
{
    [StringLength(500)]
    [SuppressMessage("Aetheus.Security", "SEC004",
        Justification = "Rotation response. The renewed agent token reaches the one agent whose heartbeat just authenticated, and returning it is how a token can be rotated without a human handling it.")]
    public string? RenewedToken { get; init; }
    public DateTime? RenewedTokenExpiresAtUtc { get; init; }

    /// <summary>
    /// When this reply's request was received and when the reply left, both on the backend's clock
    /// (T2 and T3 of <see cref="ClockSampleDto"/>). Null from a backend predating the clock measure.
    /// </summary>
    public DateTime? ReceivedAtUtc { get; init; }
    public DateTime? RepliedAtUtc { get; init; }
}

public sealed record DiskInfoDto
{
    [StringLength(260)]
    public string MountPoint { get; init; } = string.Empty;
    public double UsedGb { get; init; }
    public double TotalGb { get; init; }

    /// <summary>
    /// Inodes of the filesystem and how many are in use. Null when the agent could not read them
    /// (Windows, an agent predating the measure, a filesystem without a fixed inode table such as
    /// btrfs): unknown, never "0 % used".
    /// </summary>
    public long? InodesTotal { get; init; }
    public long? InodesUsed { get; init; }

    /// <summary>
    /// Identity of the device behind the mount (<c>/dev/sda1</c>, or its major:minor number), so that
    /// several mounts of one device are known to be the same storage. Null from an agent predating the
    /// field or on a platform that cannot tell.
    /// </summary>
    [StringLength(260)]
    public string? DeviceId { get; init; }

    /// <summary>
    /// The disks that are distinct storage. A mount of a device already listed is dropped: same
    /// <see cref="DeviceId"/>, or, from an agent that does not report it, the same size, the same inode
    /// table (a fixed property of a formatted filesystem) and the same use within 0.1 Go, which a
    /// second mount of one device always has and two different volumes hardly ever do. The first
    /// listed wins, so "/" stays ahead of a bind mount of it.
    /// </summary>
    public static List<DiskInfoDto> Distinct(IEnumerable<DiskInfoDto> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var kept = new List<DiskInfoDto>();
        foreach (var disk in disks)
        {
            if (!kept.Exists(other => SameStorage(other, disk))) kept.Add(disk);
        }
        return kept;
    }

    /// <summary>Used and total space of the distinct storage among <paramref name="disks"/>, in Go
    /// (1024³ bytes, the unit the agent reports): what the server really holds, each device counted once.</summary>
    public static (double UsedGb, double TotalGb) Totals(IEnumerable<DiskInfoDto> disks)
    {
        var distinct = Distinct(disks);
        return (distinct.Sum(disk => disk.UsedGb), distinct.Sum(disk => disk.TotalGb));
    }

    private static bool SameStorage(DiskInfoDto first, DiskInfoDto second)
    {
        if (first.DeviceId is { Length: > 0 } && second.DeviceId is { Length: > 0 })
            return string.Equals(first.DeviceId, second.DeviceId, StringComparison.Ordinal);
        if (first.DeviceId is { Length: > 0 } || second.DeviceId is { Length: > 0 }) return false;
        return first.InodesTotal is > 0
            && first.InodesTotal == second.InodesTotal
            && first.TotalGb == second.TotalGb
            && Math.Abs(first.UsedGb - second.UsedGb) <= 0.1;
    }

    /// <summary>
    /// The fullest inode table among <paramref name="disks"/>, in percent. Inodes run out per
    /// filesystem, so a sum would hide a full one behind an empty one: the highest share is the one
    /// that stops writes. Null when no disk carries an inode count.
    /// </summary>
    public static double? MaxInodeUsedPercent(IEnumerable<DiskInfoDto> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        double? highest = null;
        foreach (var disk in disks)
        {
            if (disk is not { InodesTotal: > 0 and var total, InodesUsed: >= 0 and var used }) continue;
            var percent = Math.Round(Math.Min(used, total) * 100d / total, 1);
            if (highest is null || percent > highest) highest = percent;
        }
        return highest;
    }
}

public sealed record ServerRegistrationRequest
{
    [Required]
    [StringLength(500)]
    public string RegistrationToken { get; init; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Hostname { get; init; } = string.Empty;

    [StringLength(200)]
    public string OsDescription { get; init; } = string.Empty;

    [StringLength(50)]
    public string AgentVersion { get; init; } = string.Empty;

    /// <summary>Current agent/backend contract, reported during enrollment so compatibility is
    /// authoritative before the first periodic heartbeat.</summary>
    [Range(1, int.MaxValue)]
    public int? AgentProtocolVersion { get; init; }

    /// <summary>Sorted stable identifiers available immediately after enrollment.</summary>
    [MaxLength(128)]
    [MaxItemStringLength(200)]
    public List<string>? AgentCapabilities { get; init; }

    [StringLength(45)]
    public string IpAddress { get; init; } = string.Empty;

    /// <summary>UTC instant of the agent binary install/upgrade - same source as the
    /// matching field on <see cref="ServerHeartbeatDto"/>.</summary>
    public DateTime? AgentInstalledAt { get; init; }

    /// <summary>True when the agent host has a working Docker CLI. Captured at enrollment so the
    /// capability is known before the first heartbeat.</summary>
    public bool DockerAvailable { get; init; }

    /// <summary>Agent-reported: Git workspace preparation is available. Application SDKs are
    /// resolved in containers and <see cref="DockerAvailable"/> reports the OCI capability. Defaults
    /// the per-server PipelineRunnerEnabled gate at enrollment. Nullable so a pre-feature agent reads
    /// as null and keeps the legacy "enabled" default instead of being wrongly disabled.</summary>
    public bool? PipelineRunnerAvailable { get; init; }

    /// <summary>S-DES-23: agent runs with TLS validation disabled (AllowInsecureCerts, dev only).</summary>
    public bool InsecureTls { get; init; }

    /// <summary>Carries the lowercase machine-identity fingerprint, never the raw host value, so
    /// enrollment can revive the same server. Null falls back to hostname matching.</summary>
    [StringLength(64)]
    [RegularExpression("^[0-9a-f]{64}$")]
    public string? MachineIdHash { get; init; }
}

public sealed record ServerRegistrationResponse
{
    public int ServerId { get; init; }

    [SuppressMessage("Aetheus.Security", "SEC004",
        Justification = "Enrolment response. The bearer token is issued once, to the agent that just consumed a single-use registration token, and is the only way that agent can ever authenticate afterwards.")]
    public string BearerToken { get; init; } = string.Empty;
}

/// <summary>
/// Outcome of an attempt to contact a server's agent. Agent communication is
/// poll-based (the agent pushes heartbeats); reachability is therefore derived
/// from heartbeat freshness rather than a synchronous round-trip.
/// </summary>
public sealed record ContactAgentResultDto
{
    /// <summary>True when a fresh heartbeat was observed within the freshness window.</summary>
    public bool Reachable { get; init; }

    /// <summary>Last heartbeat timestamp known to the server (UTC), if the agent ever reported.</summary>
    public DateTime? LastHeartbeat { get; init; }

    /// <summary>Seconds elapsed since the last heartbeat at the time of the check.</summary>
    public double? SecondsSinceLastHeartbeat { get; init; }

    /// <summary>Agent version from the last heartbeat, when known.</summary>
    [StringLength(50)]
    public string? AgentVersion { get; init; }

    /// <summary>Human-readable, localisation-key-free reason when <see cref="Reachable"/> is false.</summary>
    [StringLength(500)]
    public string? Error { get; init; }
}

/// <summary>Lists a retired server eligible for permanent deletion while its row and links remain
/// available for revival.</summary>
public sealed record RetiredServerDto
{
    public int Id { get; init; }

    [StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [StringLength(200)]
    public string Hostname { get; init; } = string.Empty;

    [StringLength(200)]
    public string OsDescription { get; init; } = string.Empty;

    public DateTime RetiredAt { get; init; }

    public DateTime LastHeartbeat { get; init; }

    public int OrganizationId { get; init; }

    /// <summary>True when a machine identity was recorded, so a reinstall is recognised even if the
    /// host was renamed; false means only the hostname can bring it back.</summary>
    public bool HasMachineIdentity { get; init; }
}

public sealed record UpdateServerRequest
{
    [StringLength(100)]
    public string? Name { get; init; }
    [MaxLength(20)]
    [MaxItemStringLength(50)]
    public List<string>? Tags { get; init; }
    public ServerStatus? Status { get; init; }
    public ServerType? Type { get; init; }
}

/// <summary>
/// Toggles the pipeline-runner capability on a server. Single field - kept as a record so
/// future safeguards (reason text, scheduled-window override) can be added without breaking
/// the JSON contract.
/// </summary>
public sealed record UpdatePipelineRunnerRequest
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Toggles the "containers only" policy on a runner. When enabled, the runner refuses any
/// pipeline step that is not container-isolated (enforced server-side at scheduling time).
/// </summary>
public sealed record UpdateContainerIsolationPolicyRequest
{
    public bool Required { get; init; }
}
