// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Teamspeak;

public sealed record TeamspeakDataDto
{
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    [StringLength(100)]
    public string Version { get; init; } = string.Empty;
    [StringLength(100)]
    public string Platform { get; init; } = string.Empty;
    public long UptimeSeconds { get; init; }
    public int OnlineClients { get; init; }
    public int MaxClients { get; init; }
    public int ChannelCount { get; init; }
    [StringLength(200)]
    public string ServerName { get; init; } = string.Empty;
    public int VoicePort { get; init; }
    public int QueryPort { get; init; }
    public List<TeamspeakChannelDto> Channels { get; init; } = [];
    public List<TeamspeakClientDto> Clients { get; init; } = [];
    public List<TeamspeakBanDto> Bans { get; init; } = [];
}

public sealed record TeamspeakChannelDto
{
    public int Id { get; init; }
    [StringLength(200)]
    public string Name { get; init; } = string.Empty;
    public int ParentId { get; init; }
    public int Order { get; init; }
    public int TotalClients { get; init; }
    public int MaxClients { get; init; } = -1;
    public bool IsDefault { get; init; }
    public bool HasPassword { get; init; }
    public bool IsPermanent { get; init; }
}

public sealed record TeamspeakClientDto
{
    public int ClientId { get; init; }
    [StringLength(200)]
    public string UniqueId { get; init; } = string.Empty;
    [StringLength(200)]
    public string Nickname { get; init; } = string.Empty;
    public int ChannelId { get; init; }
    [StringLength(200)]
    public string ChannelName { get; init; } = string.Empty;
    [StringLength(100)]
    public string Platform { get; init; } = string.Empty;
    [StringLength(100)]
    public string Version { get; init; } = string.Empty;
    public long IdleTimeSeconds { get; init; }
    public long ConnectionTimeSeconds { get; init; }
    public bool IsServerQuery { get; init; }
}

public sealed record TeamspeakBanDto
{
    public int BanId { get; init; }
    [StringLength(45)]
    public string Ip { get; init; } = string.Empty;
    [StringLength(200)]
    public string UniqueId { get; init; } = string.Empty;
    [StringLength(200)]
    public string Nickname { get; init; } = string.Empty;
    [StringLength(1000)]
    public string Reason { get; init; } = string.Empty;
    public long Duration { get; init; }
    public long Created { get; init; }
}

public sealed record TeamspeakActionRequest
{
    [Required]
    public TeamspeakAction Action { get; init; }
}

public sealed record TeamspeakSetupRequest
{
    [Required]
    [StringLength(500)]
    public string InstallPath { get; init; } = "/opt/teamspeak3-server_linux_amd64";

    [Range(1024, 65535)]
    public int VoicePort { get; init; } = 9987;

    [Range(1024, 65535)]
    public int QueryPort { get; init; } = 10011;
}

public sealed record TeamspeakKickRequest
{
    [Required]
    public int ClientId { get; init; }

    [StringLength(200)]
    public string ReasonMessage { get; init; } = string.Empty;
}

/// <summary>Item #9 tier-1 - move a client to another channel. Less destructive than kick.</summary>
public sealed record TeamspeakMoveClientRequest
{
    [Required]
    public int ClientId { get; init; }

    [Required]
    public int TargetChannelId { get; init; }

    /// <summary>Optional channel password if the target is locked.</summary>
    [StringLength(40)]
    public string? ChannelPassword { get; init; }
}

/// <summary>Item #9 tier-1 - 1-to-1 popup notification (distinct from server-wide message).</summary>
public sealed record TeamspeakPokeClientRequest
{
    [Required]
    public int ClientId { get; init; }

    [Required]
    [StringLength(200)] // TS3 server limit
    public string Message { get; init; } = string.Empty;
}

// ============================================================================
// Item #9 tier-2/3 - advanced ops. Each request maps 1:1 to a TS3 ServerQuery
// command (clientinfo, serversnapshot*, servergroup*, complain*, token*).
// Validation is best-effort here; the back-end re-validates and the ServerQuery
// protocol enforces argument types on its side.
// ============================================================================

/// <summary>Tier-2 - single client detail panel (TS3 <c>clientinfo clid=X</c>).</summary>
public sealed record TeamspeakClientInfoRequest
{
    [Required]
    public int ClientId { get; init; }
}

/// <summary>Tier-2 - broadcast warning, wait, kick all, restart (composed sequence).</summary>
public sealed record TeamspeakGracefulRestartRequest
{
    [Range(0, 600)]
    public int WarningSeconds { get; init; } = 60;

    [StringLength(200)]
    public string WarningMessage { get; init; } = "Server restart in {0} s, please reconnect after.";
}

/// <summary>Tier-2 - create a server snapshot (returns an opaque blob).</summary>
public sealed record TeamspeakSnapshotCreateRequest
{
    [StringLength(200)]
    public string? Label { get; init; }
}

/// <summary>Tier-2 - restore a previously created snapshot. The blob is the verbatim TS3
/// <c>serversnapshotdeploy</c> argument format. Refused if empty.</summary>
public sealed record TeamspeakSnapshotDeployRequest
{
    [Required]
    [StringLength(5_000_000)] // 5 MB - snapshot blobs grow with channel/permission count
    public string SnapshotBlob { get; init; } = string.Empty;

    /// <summary>Operator must type the server name to confirm - guards against fat-finger
    /// deploys that wipe channels/groups/permissions.</summary>
    [Required]
    [StringLength(200)]
    public string Confirmation { get; init; } = string.Empty;
}

/// <summary>Tier-2 - add a client to a server group.</summary>
public sealed record TeamspeakServerGroupAddRequest
{
    [Required]
    public int ServerGroupId { get; init; }

    [Required]
    public int ClientDatabaseId { get; init; }
}

/// <summary>Tier-2 - remove a client from a server group.</summary>
public sealed record TeamspeakServerGroupRemoveRequest
{
    [Required]
    public int ServerGroupId { get; init; }

    [Required]
    public int ClientDatabaseId { get; init; }
}

/// <summary>Tier-2 - mint a new ServerQuery / server-admin token. Refused without permissions.</summary>
public sealed record TeamspeakTokenCreateRequest
{
    /// <summary>Token target type - 0 = server group, 1 = channel group.</summary>
    [Range(0, 1)]
    public int Type { get; init; }

    [Required]
    public int GroupId { get; init; }

    /// <summary>For channel-group tokens, the channel id. 0 for server-group tokens.</summary>
    public int ChannelId { get; init; }

    [StringLength(200)]
    public string? Description { get; init; }
}

/// <summary>Tier-2 - delete a previously created token.</summary>
public sealed record TeamspeakTokenDeleteRequest
{
    [Required]
    [StringLength(200)]
    public string Token { get; init; } = string.Empty;
}

/// <summary>Tier-3 - dismiss a complaint (admin moderator action).</summary>
public sealed record TeamspeakComplaintDeleteRequest
{
    [Required]
    public int TargetClientDatabaseId { get; init; }

    /// <summary>If null/empty, deletes ALL complaints for the target client. Otherwise targets
    /// a specific complaint from the source client.</summary>
    public int? SourceClientDatabaseId { get; init; }
}

public sealed record TeamspeakBanRequest
{
    [Required]
    [StringLength(200)]
    public string ClientUniqueId { get; init; } = string.Empty;

    [Range(0, 315360000)]
    public int DurationSeconds { get; init; }

    [StringLength(200)]
    public string Reason { get; init; } = string.Empty;
}

public sealed record TeamspeakCreateChannelRequest
{
    [Required]
    [StringLength(40)]
    public string Name { get; init; } = string.Empty;

    public int? ParentId { get; init; }

    [StringLength(40)]
    public string? Password { get; init; }

    public int? MaxClients { get; init; }

    public bool IsPermanent { get; init; } = true;
}

public sealed record TeamspeakEditChannelRequest
{
    [Required]
    public int ChannelId { get; init; }

    [StringLength(40)]
    public string? Name { get; init; }

    [StringLength(40)]
    public string? Password { get; init; }

    public int? MaxClients { get; init; }
}

public sealed record TeamspeakServerEditRequest
{
    [StringLength(64)]
    public string? ServerName { get; init; }

    [StringLength(40)]
    public string? Password { get; init; }

    [Range(1, 32)]
    public int? MaxClients { get; init; }

    [StringLength(1024)]
    public string? WelcomeMessage { get; init; }
}

public sealed record TeamspeakGlobalMessageRequest
{
    [Required]
    [StringLength(1024)]
    public string Message { get; init; } = string.Empty;
}

public sealed record TeamspeakLogRequest
{
    [Range(1, 5000)]
    public int Lines { get; init; } = 100;
}

/// <summary> The values the clients grid's checkable Platform filter offers. The grid is loaded
/// page by page, so the platforms present across every connected client of the server come from the
/// API. </summary>
public sealed record TeamspeakFilterValuesDto
{
    public List<string> Platforms { get; init; } = [];
}
