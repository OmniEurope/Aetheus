// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class RegistryPackageVersion
{
    public int Id { get; set; }
    public int RegistryPackageId { get; set; }
    public string Version { get; set; } = string.Empty;
    public string NormalizedVersion { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Sha1 { get; set; } = string.Empty;
    public string Integrity { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
    public bool IsPrerelease { get; set; }
    public bool IsListed { get; set; } = true;
    public string PublishedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public RegistryPackage RegistryPackage { get; set; } = null!;
    public List<RegistryPackageFile> Files { get; set; } = [];
}
