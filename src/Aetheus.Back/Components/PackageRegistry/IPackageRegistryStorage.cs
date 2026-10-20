// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.PackageRegistry;

public interface IPackageRegistryStorage
{
    Task<StoredPackagePayload> SaveAsync(
        PackageRegistryKind kind,
        string normalizedName,
        string normalizedVersion,
        string extension,
        Stream content,
        CancellationToken ct = default);

    /// <summary>Stores one named file of a version (formats whose version is a set of files, such as
    /// Maven). Same immutability as <see cref="SaveAsync"/>: identical bytes are accepted again,
    /// different bytes under the same name are a conflict.</summary>
    Task<StoredPackagePayload> SaveFileAsync(
        PackageRegistryKind kind,
        string normalizedName,
        string normalizedVersion,
        string fileName,
        Stream content,
        CancellationToken ct = default);

    Stream? OpenRead(string relativePath);
    Task DeleteAsync(string relativePath, CancellationToken ct = default);
    Task<int> ReconcileAsync(
        IReadOnlySet<string> referencedPaths,
        DateTime orphanCutoffUtc,
        CancellationToken ct = default);
}

public sealed record StoredPackagePayload(
    string RelativePath,
    long SizeBytes,
    string Sha256,
    string Sha1,
    string Integrity,
    bool CreatedNew = false,
    string Md5 = "")
{
    /// <summary>The SHA-512 digest in lower-case hex, read back from the npm-style integrity string.</summary>
    public string Sha512Hex => Integrity.StartsWith("sha512-", StringComparison.Ordinal)
        ? Convert.ToHexStringLower(Convert.FromBase64String(Integrity["sha512-".Length..]))
        : string.Empty;
}
