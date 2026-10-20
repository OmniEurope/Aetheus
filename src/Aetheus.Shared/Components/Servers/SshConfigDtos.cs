// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Servers;

/// <summary>
/// What the agent could read of the host's SSH server configuration and of the authorized_keys
/// files, without any privilege of its own. A value the agent could not establish is null
/// ("unknown"), never a guessed default: an unreadable include read before the setting, or an
/// unreadable authorized_keys file, says so in <see cref="UnreadableFiles"/>.
/// </summary>
public sealed record SshConfigDataDto
{
    /// <summary>False when the host has no OpenSSH server configuration to read (Windows, or no
    /// <c>/etc/ssh/sshd_config</c>); every other field is then empty.</summary>
    public bool Collected { get; init; }

    public SshSettingDto PermitRootLogin { get; init; } = new();
    public SshSettingDto PasswordAuthentication { get; init; } = new();

    /// <summary>Where sshd looks for keys. A change moves the keys out of what is watched.</summary>
    public SshSettingDto AuthorizedKeysFile { get; init; } = new();

    /// <summary>Configuration files (main file or includes) the agent could not read.</summary>
    [MaxLength(64)]
    [MaxItemStringLength(500)]
    public List<string> UnreadableFiles { get; init; } = [];

    /// <summary>One entry per authorized_keys file looked at: root's and every login user's.</summary>
    [MaxLength(64)]
    public List<AuthorizedKeysFileDto> AuthorizedKeys { get; init; } = [];
}

/// <summary>The global (outside any Match block) effective value of one sshd keyword.</summary>
public sealed record SshSettingDto
{
    /// <summary>Lower-case effective value; null when it could not be established.</summary>
    [StringLength(500)]
    public string? Value { get; init; }

    /// <summary><c>file:line</c> that set it, <c>default</c> when no file sets it, or null when unknown.</summary>
    [StringLength(600)]
    public string? Source { get; init; }

    /// <summary>A Match block sets the keyword too: some connections may get another value.</summary>
    public bool OverriddenInMatch { get; init; }
}

public enum AuthorizedKeysFileState
{
    /// <summary>The file was read; <see cref="AuthorizedKeysFileDto.Keys"/> lists its keys.</summary>
    Read,
    /// <summary>The file does not exist: no key is authorized through it.</summary>
    Absent,
    /// <summary>The agent may not read it (for example <c>/root</c> is 0700): its keys are unknown.</summary>
    Unreadable
}

public sealed record AuthorizedKeysFileDto
{
    [StringLength(100)]
    public string User { get; init; } = string.Empty;

    [StringLength(500)]
    public string Path { get; init; } = string.Empty;

    public AuthorizedKeysFileState State { get; init; }

    [MaxLength(256)]
    public List<AuthorizedKeyDto> Keys { get; init; } = [];
}

/// <summary>A key line reduced to its type and fingerprint; the key itself never leaves the host.</summary>
public sealed record AuthorizedKeyDto
{
    [StringLength(100)]
    public string KeyType { get; init; } = string.Empty;

    /// <summary><c>SHA256:...</c> as <c>ssh-keygen -lf</c> prints it, or <c>LINE-SHA256:...</c>, the
    /// hash of the whole line, when the line could not be parsed as a key.</summary>
    [StringLength(100)]
    public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>The stored SSH snapshot of a server and the changes detected against it.</summary>
public sealed record SshConfigStatusDto
{
    public SshConfigDataDto Snapshot { get; init; } = new();

    /// <summary>When this snapshot was first reported (it has been unchanged since).</summary>
    public DateTime ObservedAt { get; init; }

    /// <summary>When the first snapshot, the baseline, was recorded.</summary>
    public DateTime BaselineAt { get; init; }

    /// <summary>Most recent first.</summary>
    [MaxLength(SshConfigLimits.MaxStoredChanges)]
    public List<SshConfigChangeDto> RecentChanges { get; init; } = [];
}

public sealed record SshConfigChangeDto
{
    public DateTime DetectedAt { get; init; }

    /// <summary><c>Critical</c> for a root login opened or a key added, <c>Warning</c> otherwise.</summary>
    [StringLength(20)]
    public string Severity { get; init; } = string.Empty;

    [StringLength(1000)]
    public string Message { get; init; } = string.Empty;
}

public static class SshConfigLimits
{
    public const int MaxStoredChanges = 20;
}
