// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.PackageRegistry;

public sealed record PackageRegistryPackageDto
{
    public int Id { get; init; }
    public PackageRegistryKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? LatestVersion { get; init; }
    public int VersionCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record PackageRegistryPackageDetailDto
{
    public int Id { get; init; }
    public PackageRegistryKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<PackageRegistryVersionDto> Versions { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record PackageRegistryVersionDto
{
    public int Id { get; init; }
    public string Version { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public bool IsListed { get; init; }
    public bool IsDeprecated { get; init; }
    public string PublishedBy { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

public sealed record UpdatePackageRegistryVersionRequest
{
    public bool IsListed { get; init; }
}

public sealed record UpdatePackageRegistryVersionDeprecationRequest
{
    public bool IsDeprecated { get; init; }
}
