// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Shared;

/// <summary>
/// Pure entity → DTO mapping and the well-known-service catalogue shared by <see cref="ServerService"/>
/// and its heartbeat/management collaborators. Extracted from the former <c>ServerService.Mapping.cs</c>
/// partial (plus the mapping helpers that lived on the main file) into a real collaborator - no
/// <c>partial</c> split.
/// </summary>
internal static class ServerDataMapper
{
    public static readonly Dictionary<string, ServiceType> WellKnownManageableServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nginx"] = ServiceType.Systemd,
        ["apache2"] = ServiceType.Systemd,
        ["docker"] = ServiceType.Systemd,
        ["fail2ban"] = ServiceType.Systemd,
        ["rkhunter"] = ServiceType.Systemd,
        ["ufw"] = ServiceType.Systemd,
        ["portsentry"] = ServiceType.Systemd,
        ["postfix"] = ServiceType.Systemd,
        ["dovecot"] = ServiceType.Systemd,
        ["mysql"] = ServiceType.Systemd,
        ["mariadb"] = ServiceType.Systemd,
        ["postgresql"] = ServiceType.Systemd,
        ["redis-server"] = ServiceType.Systemd,
        ["mongod"] = ServiceType.Systemd,
        ["certbot"] = ServiceType.Systemd,
    };

    public static readonly HashSet<string> SystemCriticalServices =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "sshd", "cron", "rsyslog", "systemd-journald",
            "systemd-logind", "systemd-udevd", "dbus", "auditd"
        };

    public static bool ResolveManageable(string name, bool agentValue)
    {
        if (SystemCriticalServices.Contains(name)) return false;
        if (WellKnownManageableServices.ContainsKey(name)) return true;
        return agentValue;
    }

    public static ServerDto MapToDto(Server s)
    {
        // S-TECH-CUNK: on the list/tile the three pipeline-capability flags are tri-state. A server that
        // has never phoned home (no heartbeat) has UNKNOWN capabilities (null) - genuinely distinct from a
        // heartbeated agent that lacks the grant (false). Any heartbeat sets LastHeartbeat, so once the
        // agent has reported at least once the flags are known and pass straight through.
        var reported = s.LastHeartbeat != default;
        return new()
        {
            Id = s.Id,
            Name = s.Name,
            Hostname = s.Hostname,
            OsDescription = s.OsDescription,
            AgentVersion = s.AgentVersion,
            AgentProtocolVersion = s.AgentProtocolVersion,
            AgentCapabilities = DeserializeDiagnostics(s.AgentCapabilitiesJson),
            AgentUpdateReserved = s.AgentUpdateReserved,
            Status = s.Status,
            Type = s.Type,
            LastHeartbeat = s.LastHeartbeat,
            Tags = TagsHelper.DeserializeTags(s.Tags),
            CreatedAt = s.CreatedAt,
            OrganizationId = s.OrganizationId,
            AgentInstalledAt = s.AgentInstalledAt,
            ExternalProbe = s.ExternalProbe,
            ExternalProbeAt = s.ExternalProbeAt,
            ExternalProbeTarget = s.ExternalProbeTarget,
            PipelineRunnerEnabled = reported ? s.PipelineRunnerEnabled : null,
            DockerAvailable = s.DockerAvailable,
            PackageManagementAvailable = reported ? s.PackageManagementAvailable : null,
            PatchManagementAvailable = reported ? s.PatchManagementAvailable : null,
            FirewallManagementAvailable = reported ? s.FirewallManagementAvailable : null,
            PortObservationAvailable = reported ? s.PortObservationAvailable : null,
            PortsObservedAt = s.PortsObservedAt,
            DeploymentTargetAvailable = reported ? s.DeploymentTargetAvailable : null,
            MailSetupAvailable = s.MailSetupAvailable,
            TeamspeakSetupAvailable = s.TeamspeakSetupAvailable,
            RequireContainerIsolation = s.RequireContainerIsolation,
            InsecureTls = s.InsecureTls,
            CapabilityDiagnostics = DeserializeDiagnostics(s.CapabilityDiagnosticsJson)
            ,
            ScannerCapabilities = DeserializeDiagnostics(s.ScannerCapabilitiesJson)
        };
    }

    /// <summary>S-TECH-CDUI: the persisted capability-diagnostics JSON array back into a list (empty on
    /// null / malformed - a diagnostics blob must never break the server DTO).</summary>
    internal static List<string> DeserializeDiagnostics(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static List<ServiceInfoDto> BuildServicesWithWellKnown(List<ServiceInfo> detected)
    {
        // Stored service rows represent installed services reported by the agent and carry no
        // separate installed flag.
        var dtos = detected.Select(s => new ServiceInfoDto
        {
            Name = s.Name,
            Type = s.Type,
            Status = s.Status,
            IsRunning = s.IsRunning,
            IsManageable = ResolveManageable(s.Name, s.IsManageable),
            IsInstalled = true
        }).ToList();
        return SupplementWithWellKnown(dtos);
    }

    public static List<ServiceInfoDto> SupplementWithWellKnown(List<ServiceInfoDto> services)
    {
        // Preserve the agent reported installed flag.
        var result = services.Select(s => s with
        {
            IsManageable = ResolveManageable(s.Name, s.IsManageable)
        }).ToList();

        var detectedNames = new HashSet<string>(result.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in WellKnownManageableServices)
        {
            if (detectedNames.Contains(name)) continue;
            result.Add(new ServiceInfoDto
            {
                Name = name,
                Type = type,
                Status = "Not installed",
                IsRunning = false,
                IsManageable = true,
                IsInstalled = false
            });
        }

        return result;
    }

    public static ServerTaskDto MapTaskDto(ServerTask task) =>
        TaskDtoMapper.ToServerDetailDto(task);

    public static DockerDataDto MapDockerDataDto(Server server) => new()
    {
        Containers = MapDockerContainers(server.DockerContainers),
        Images = server.DockerImages.Select(i => new DockerImageDto
        {
            ImageId = i.ImageId,
            Repository = i.Repository,
            Tag = i.Tag,
            Size = i.Size,
            Created = i.Created
        }).ToList(),
        ComposeStacks = server.DockerComposeStacks.Select(s => new DockerComposeStackDto
        {
            Name = s.Name,
            Status = s.Status,
            ConfigFile = s.ConfigFile,
            RunningCount = s.RunningCount,
            TotalCount = s.TotalCount
        }).ToList(),
        Networks = server.DockerNetworks.Select(n => new DockerNetworkDto
        {
            NetworkId = n.NetworkId,
            Name = n.Name,
            Driver = n.Driver,
            Scope = n.Scope
        }).ToList(),
        Volumes = server.DockerVolumes.Select(v => new DockerVolumeDto
        {
            Name = v.Name,
            Driver = v.Driver,
            Mountpoint = v.Mountpoint
        }).ToList()
    };

    public static List<DockerContainerDto> MapDockerContainers(
        IEnumerable<DockerContainer> containers) =>
        containers.Select(container => new DockerContainerDto
        {
            ContainerId = container.ContainerId,
            Name = container.Name,
            Image = container.Image,
            State = container.State,
            Status = container.Status,
            Ports = container.Ports,
            Created = container.Created,
            CpuPercent = container.CpuPercent,
            MemoryUsageMb = container.MemoryUsageMb,
            MemoryLimitMb = container.MemoryLimitMb,
            SecurityInspected = container.SecurityInspected,
            Risks = container.Risks,
            DangerousCapabilities = container.DangerousCapabilities,
            AetheusRole = container.AetheusRole
        }).ToList();

    public static ApacheDataDto MapApacheDataDto(Server server) => new()
    {
        IsInstalled = server.ApacheState is not null,
        IsRunning = server.ApacheState?.IsRunning ?? false,
        Version = server.ApacheState?.Version ?? string.Empty,
        Pid = server.ApacheState?.Pid,
        ConfigRoot = server.ApacheState?.ConfigRoot ?? string.Empty,
        CollectionDegraded = server.ApacheState?.CollectionDegraded ?? false,
        CollectionDiagnostics = server.ApacheState?.CollectionDiagnostics ?? string.Empty,
        Modules = server.ApacheModules.Select(m => new ApacheModuleDto
        {
            Name = m.Name,
            Type = m.Type,
            IsEnabled = m.IsEnabled
        }).ToList(),
        VirtualHosts = server.ApacheVirtualHosts.Select(v => new ApacheVirtualHostDto
        {
            ServerName = v.ServerName,
            Port = v.Port,
            DocumentRoot = v.DocumentRoot,
            ConfigFile = v.ConfigFile,
            IsEnabled = v.IsEnabled
        }).ToList()
    };

    public static CertbotDataDto MapCertbotDataDto(Server server) => new()
    {
        IsInstalled = server.CertbotState is not null || server.CertbotCertificates.Count > 0,
        Version = string.Empty,
        Certificates = server.CertbotCertificates.Select(c => new CertbotCertificateDto
        {
            Name = c.Name,
            Domains = TagsHelper.DeserializeTags(c.Domains),
            ExpiryDate = c.ExpiryDate,
            CertPath = c.CertPath,
            KeyPath = c.KeyPath,
            Authenticator = c.Authenticator,
            WebrootPath = c.WebrootPath,
            RenewalConvention = c.RenewalConvention
        }).ToList(),
        RenewalCheckedAt = server.CertbotState?.RenewalCheckedAt,
        RenewalCheckSucceeded = server.CertbotState?.RenewalCheckSucceeded,
        HelperVersion = server.CertbotState?.HelperVersion ?? 0,
        RenewalCheckFailures = ReadRenewalFailures(server.CertbotState?.RenewalCheckFailuresJson)
    };

    // Recette R3-56: an unreadable column reads as no reported failure, never as a crash of the page.
    private static List<CertbotRenewalFailureDto> ReadRenewalFailures(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<CertbotRenewalFailureDto>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static CronDataDto MapCronDataDto(Server server) => new()
    {
        // Cron state is not persisted yet; populated from heartbeat in real time.
        IsInstalled = server.Services.Any(s =>
            s.Name.Equals("cron", StringComparison.OrdinalIgnoreCase) ||
            s.Name.Equals("crond", StringComparison.OrdinalIgnoreCase)),
        Jobs = []
    };

    public static MailDataDto MapMailDataDto(Server server) => MailStateProjector.ToDto(server.MailState);

    public static TeamspeakDataDto MapTeamspeakDataDto(Server server) => new()
    {
        IsInstalled = server.TeamspeakState is not null,
        IsRunning = server.TeamspeakState?.IsRunning ?? false,
        Version = server.TeamspeakState?.Version ?? string.Empty,
        Platform = server.TeamspeakState?.Platform ?? string.Empty,
        UptimeSeconds = server.TeamspeakState?.UptimeSeconds ?? 0,
        OnlineClients = server.TeamspeakState?.OnlineClients ?? 0,
        MaxClients = server.TeamspeakState?.MaxClients ?? 0,
        ChannelCount = server.TeamspeakState?.ChannelCount ?? 0,
        ServerName = server.TeamspeakState?.ServerName ?? string.Empty,
        VoicePort = server.TeamspeakState?.VoicePort ?? 0,
        QueryPort = server.TeamspeakState?.QueryPort ?? 0
    };

    public static PortsentryDataDto MapPortsentryDataDto(Server server) => new()
    {
        IsInstalled = server.PortsentryState is not null,
        IsRunning = server.PortsentryState?.IsRunning ?? false,
        Version = server.PortsentryState?.Version ?? string.Empty,
        Mode = server.PortsentryState?.Mode ?? string.Empty,
        TcpPorts = server.PortsentryState?.TcpPorts ?? string.Empty,
        UdpPorts = server.PortsentryState?.UdpPorts ?? string.Empty,
        BlockedCount = server.PortsentryState?.BlockedCount ?? 0
    };

    public static RkhunterDataDto MapRkhunterDataDto(Server server) => new()
    {
        IsInstalled = server.RkhunterState is not null,
        Version = server.RkhunterState?.Version ?? string.Empty,
        DatabaseVersion = server.RkhunterState?.DatabaseVersion ?? string.Empty,
        LastScanTime = server.RkhunterState?.LastScanTime ?? default,
        LastScanStatus = server.RkhunterState?.LastScanStatus ?? string.Empty,
        WarningCount = server.RkhunterState?.WarningCount ?? 0,
        DatabaseLastUpdated = server.RkhunterState?.DatabaseLastUpdated ?? default,
        ScanScheduleCron = server.RkhunterState?.ScanScheduleCron
    };
}
