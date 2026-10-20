// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// One stored file of a package version, for the formats whose version is a set of files rather than
/// a single archive (Maven: POM, JAR, sources, javadoc, signatures, snapshot builds).
/// </summary>
public class RegistryPackageFile
{
    public int Id { get; set; }
    public int RegistryPackageVersionId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Sha1 { get; set; } = string.Empty;
    public string Md5 { get; set; } = string.Empty;
    public string Sha512 { get; set; } = string.Empty;
    public string PublishedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public RegistryPackageVersion RegistryPackageVersion { get; set; } = null!;
}
