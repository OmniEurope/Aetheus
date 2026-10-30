// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class RegistryPackage
{
    public int Id { get; set; }
    public PackageRegistryKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? DistTagsJson { get; set; }

    /// <summary>Whether a client without credentials may read this package (version list, metadata,
    /// archive), so a deploy image build on an agent without a token can restore it (R-58). Off by
    /// default; only an administrator changes it, and it never opens publication.</summary>
    public bool AllowAnonymousDownload { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<RegistryPackageVersion> Versions { get; set; } = [];
}
