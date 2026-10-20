// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Vaults;

public sealed record VaultDto : OwnedResourceDto
{
    public int SecretCount { get; init; }
}

public sealed record VaultDetailDto : OwnedResourceDto
{
    public int SecretCount { get; init; }
    public List<VaultSecretDto> Secrets { get; init; } = [];
}

public sealed record VaultSecretDto
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public int VersionCount { get; init; }
}

/// <summary>One secret's clear value, answered to the copy button only.</summary>
public sealed record RevealedSecretValueDto
{
    public string Value { get; init; } = string.Empty;
}

public sealed record VaultSecretVersionDto
{
    public int Version { get; init; }
    public string Key { get; init; } = string.Empty;
    public DateTime ChangedAt { get; init; }
    public ChangeType ChangeType { get; init; }
    /// <summary>The author, when recorded for this version.</summary>
    public string? ChangedBy { get; init; }
}

[AtMostOneOwner]
public sealed record CreateVaultRequest : OwnedResourceRequest;

[AtMostOneOwner]
public sealed record UpdateVaultRequest : VersionedOwnedResourceRequest;

public sealed record CreateVaultSecretRequest : KeyValueRequest
{
    [Required]
    [StringLength(MaxValueLength)]
    public override string Value { get; init; } = string.Empty;

    public DateTime? ExpiresAt { get; init; }
}

public sealed record UpdateVaultSecretRequest : KeyValueRequest
{
    [Required]
    [StringLength(MaxValueLength)]
    public override string Value { get; init; } = string.Empty;

    public DateTime? ExpiresAt { get; init; }
}

/// <summary>
/// Rotation request - same payload as an update but recorded as a Rotated version
/// in the audit history rather than an in-place edit.
/// </summary>
public sealed record RotateVaultSecretRequest
{
    [Required]
    [StringLength(KeyValueRequest.MaxValueLength)]
    public string Value { get; init; } = string.Empty;

    public DateTime? ExpiresAt { get; init; }
}

/// <summary> The values the vaults list's checkable column filters offer. The list is loaded page
/// by page, so the project names present across every vaults the caller can read come from the API
/// rather than from the rows on screen. </summary>
public sealed record VaultFilterValuesDto
{
    public List<string> ProjectNames { get; init; } = [];
}
