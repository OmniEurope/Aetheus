// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Apache;

// --- Aggregate heartbeat data ---

public sealed record ApacheDataDto
{
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    [StringLength(255)]
    public string Version { get; init; } = string.Empty;
    public int? Pid { get; init; }
    [StringLength(255)]
    public string ConfigRoot { get; init; } = string.Empty;
    [MaxLength(2048)]
    public List<ApacheModuleDto> Modules { get; init; } = [];
    [MaxLength(2048)]
    public List<ApacheVirtualHostDto> VirtualHosts { get; init; } = [];

    /// <summary>
    /// True when Apache is installed but the agent could not reliably enumerate vhosts/modules
    /// (e.g. <c>apache2ctl -S</c> failed for lack of privileges). The lists above may then be
    /// empty or incomplete and should not be trusted as the real server state.
    /// </summary>
    public bool CollectionDegraded { get; init; }

    /// <summary>Short human-readable reason the collection degraded (command + exit codes).</summary>
    [StringLength(1000)]
    public string CollectionDiagnostics { get; init; } = string.Empty;
}

// --- Module info ---

public sealed record ApacheModuleDto
{
    [StringLength(255)]
    public string Name { get; init; } = string.Empty;
    [StringLength(255)]
    public string Type { get; init; } = string.Empty; // "static" or "shared"
    public bool IsEnabled { get; init; }
}

// --- Virtual host info ---

public sealed record ApacheVirtualHostDto
{
    [StringLength(255)]
    public string ServerName { get; init; } = string.Empty;
    public int Port { get; init; }
    [StringLength(1000)]
    public string DocumentRoot { get; init; } = string.Empty;
    [StringLength(1000)]
    public string ConfigFile { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }

    /// <summary>Recette R-14: the account that owns the site file when it is not the agent's own (root, certbot), so the
    /// agent cannot delete it. Empty when the agent owns the file, when its owner could not be read, and for an agent
    /// too old to report it: no owner is ever guessed.</summary>
    [StringLength(64)]
    public string ConfigFileForeignOwner { get; init; } = string.Empty;
}

// --- Action request ---

public sealed record ApacheActionRequest
{
    [Required]
    public Aetheus.Shared.Components.Apache.ApacheAction Action { get; init; }

    [StringLength(200, MinimumLength = 1)]
    public string? TargetName { get; init; }
}

// --- Log request ---

public sealed record ApacheLogRequest
{
    [Required]
    [StringLength(20)]
    [RegularExpression("^(error|access)$")]
    public string LogType { get; init; } = "error";

    [Range(1, 10000)]
    public int Lines { get; init; } = 100;
}

// --- VHost config ---

public sealed record ApacheVHostConfigDto
{
    [Required, StringLength(255)]
    public string SiteName { get; init; } = string.Empty;
    [StringLength(100_000)]
    public string Content { get; init; } = string.Empty;
}

public sealed record ApacheVHostSaveRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [RegularExpression(@"^[a-zA-Z0-9._-]+$")]
    public string SiteName { get; init; } = string.Empty;

    [Required]
    [StringLength(100000)]
    public string Content { get; init; } = string.Empty;
}

// --- .htaccess ---

public sealed record ApacheHtaccessSaveRequest
{
    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string DocumentRoot { get; init; } = string.Empty;

    [Required]
    [StringLength(100000)]
    public string Content { get; init; } = string.Empty;
}
