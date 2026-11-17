// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.AppBackups;

/// <summary>Read view of a backup policy (ADR-024 4.3). Never carries the DB password.</summary>
public sealed record BackupPolicyDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public int ServerId { get; init; }
    public string? ServerName { get; init; }
    public BackupDbEngine DbEngine { get; init; }
    public string? DbHost { get; init; }
    public int? DbPort { get; init; }
    public string? DbName { get; init; }
    public string? DbUser { get; init; }

    /// <summary>Whether a DB password is stored (the password itself is never returned).</summary>
    public bool HasPassword { get; init; }

    [MaxLength(200)]
    [MaxItemStringLength(4096)]
    public List<string> FilePaths { get; init; } = [];
    public string ScheduleCron { get; init; } = string.Empty;
    public int RetentionCount { get; init; }
    public string? RestoreCheckCron { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? LastRestoreCheckAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Read view of one backup run (ADR-024 4.3).</summary>
public sealed record BackupRunDto
{
    public int Id { get; init; }
    public int BackupPolicyId { get; init; }
    public int ServerId { get; init; }
    public BackupRunStatus Status { get; init; }
    public long SizeBytes { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public RestoreCheckStatus RestoreCheckStatus { get; init; }
    public DateTime? RestoreCheckedAt { get; init; }
    public string? RestoreCheckMessage { get; init; }
    public string? Message { get; init; }
}

/// <summary>Create a backup policy (ADR-024 4.3). The DB password is AES-encrypted server-side.</summary>
public abstract record BackupPolicyRequest
{
    public BackupDbEngine DbEngine { get; init; }

    [StringLength(255)]
    public string? DbHost { get; init; }

    [Range(1, 65535)]
    public int? DbPort { get; init; }

    [StringLength(120)]
    public string? DbName { get; init; }

    [StringLength(120)]
    public string? DbUser { get; init; }

    [StringLength(256)]
    public string? DbPassword { get; init; }

    [MaxLength(200)]
    [MaxItemStringLength(4096)]
    public List<string> FilePaths { get; init; } = [];

    [Required]
    [StringLength(120)]
    public string ScheduleCron { get; init; } = string.Empty;

    [Range(1, 365)]
    public int RetentionCount { get; init; } = 7;

    [StringLength(120)]
    public string? RestoreCheckCron { get; init; }
}

/// <summary>Create a backup policy (ADR-024 4.3). The DB password is AES-encrypted server-side.</summary>
public sealed record CreateBackupPolicyRequest : BackupPolicyRequest
{
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int ProjectId { get; init; }

    [Range(1, int.MaxValue)]
    public int ServerId { get; init; }
}

/// <summary>Update a backup policy. A null <see cref="DbPassword"/> leaves the stored password unchanged.</summary>
public sealed record UpdateBackupPolicyRequest : BackupPolicyRequest
{
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

}

/// <summary>Replaces only the backup schedule of a policy (recette R-8, a move in the planner): the five-field cron
/// expression (minutes first, UTC). Neither the restore-check cadence nor what the agent reads or connects to changes.</summary>
public sealed record SetBackupScheduleRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string ScheduleCron { get; init; } = string.Empty;
}

/// <summary>Agent-&gt;backend result of a backup execution (ADR-024 4.3).</summary>
public sealed record BackupExecuteResultDto
{
    public bool Success { get; init; }
    [StringLength(4096)]
    public string? ArchivePath { get; init; }
    public long SizeBytes { get; init; }
    [StringLength(64)]
    public string? Sha256 { get; init; }
    [StringLength(2000)]
    public string? Message { get; init; }
}

/// <summary>Agent-&gt;backend result of a restore-check (ADR-024 4.3). No-fake: success is only true when a
/// real restore onto a throwaway target actually verified.</summary>
public sealed record RestoreCheckResultDto
{
    public bool Verified { get; init; }
    [StringLength(2000)]
    public string? Message { get; init; }
}
