// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.PackageRegistry;

internal sealed class PackageRegistryStorage(IConfiguration configuration) : IPackageRegistryStorage
{
    private readonly string _basePath = configuration.GetValue("PackageRegistry:BasePath", "./data/packages")!;
    private readonly long _maxPackageBytes = PackageRegistryDefaults.ResolveMaxPackageBytes(configuration);
    private readonly long _volumeQuotaBytes = Math.Max(
        PackageRegistryDefaults.ResolveMaxPackageBytes(configuration),
        configuration.GetValue("PackageRegistry:VolumeQuotaBytes", 20L * 1024 * 1024 * 1024));

    public Task<StoredPackagePayload> SaveAsync(
        PackageRegistryKind kind,
        string normalizedName,
        string normalizedVersion,
        string extension,
        Stream content,
        CancellationToken ct = default)
        => SaveToAsync(VersionDirectory(kind, normalizedName, normalizedVersion, "package" + extension), content, ct);

    public Task<StoredPackagePayload> SaveFileAsync(
        PackageRegistryKind kind,
        string normalizedName,
        string normalizedVersion,
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        // The file name is the client's, already held to a strict character set by the caller; it
        // still never chooses a directory: the separators are refused here and the resolved path is
        // checked against the storage root.
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\', ':']) >= 0
            || fileName is "." or "..")
            throw new BadRequestException("Invalid package file name.");
        return SaveToAsync(VersionDirectory(kind, normalizedName, normalizedVersion, Path.Combine("files", fileName)),
            content, ct);
    }

    private static string VersionDirectory(
        PackageRegistryKind kind, string normalizedName, string normalizedVersion, string leaf)
    {
        var nameHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedName)));
        var versionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedVersion)));
        return Path.Combine(kind.ToString().ToLowerInvariant(), nameHash, versionHash, leaf);
    }

    private async Task<StoredPackagePayload> SaveToAsync(string relativePath, Stream content, CancellationToken ct)
    {
        var fullPath = SafeResolvePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var completed = false;
        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            // MD5 only feeds the .md5 checksum file Maven clients still request; it guards nothing.
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            await using var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                size += read;
                if (size > _maxPackageBytes)
                    throw new BadRequestException($"Package exceeds the {_maxPackageBytes} byte registry limit.");

                sha256.AppendData(buffer, 0, read);
                sha1.AppendData(buffer, 0, read);
                sha512.AppendData(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Close();
            var sha256Bytes = sha256.GetHashAndReset();
            var sha1Bytes = sha1.GetHashAndReset();
            var sha512Bytes = sha512.GetHashAndReset();
            var md5Hex = Convert.ToHexStringLower(md5.GetHashAndReset());
            var createdNew = false;
            await using var quotaLease = await AcquireQuotaLeaseAsync(ct).ConfigureAwait(false);
            if (File.Exists(fullPath))
            {
                if (!await ExistingContentMatchesAsync(fullPath, size, sha256Bytes, ct).ConfigureAwait(false))
                    throw new ConflictException("This package version already has different stored content.");

                File.Delete(temporaryPath);
                completed = true;
                return new StoredPackagePayload(
                    relativePath,
                    size,
                    Convert.ToHexStringLower(sha256Bytes),
                    Convert.ToHexStringLower(sha1Bytes),
                    "sha512-" + Convert.ToBase64String(sha512Bytes),
                    createdNew,
                    md5Hex);
            }

            var occupiedBytes = Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, quotaLease.Name, StringComparison.OrdinalIgnoreCase))
                .Sum(path => new FileInfo(path).Length);
            if (occupiedBytes > _volumeQuotaBytes)
                throw new BadRequestException("Package registry storage quota is exhausted.");
            try
            {
                File.Move(temporaryPath, fullPath);
                createdNew = true;
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                if (!await ExistingContentMatchesAsync(fullPath, size, sha256Bytes, ct).ConfigureAwait(false))
                    throw new ConflictException("This package version already has different stored content.");
                File.Delete(temporaryPath);
            }
            completed = true;

            return new StoredPackagePayload(
                relativePath,
                size,
                Convert.ToHexStringLower(sha256Bytes),
                Convert.ToHexStringLower(sha1Bytes),
                "sha512-" + Convert.ToBase64String(sha512Bytes),
                createdNew,
                md5Hex);
        }
        finally
        {
            if (!completed && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public Stream? OpenRead(string relativePath)
    {
        var fullPath = SafeResolvePath(relativePath);
        return File.Exists(fullPath)
            ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
    }

    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = SafeResolvePath(relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<int> ReconcileAsync(
        IReadOnlySet<string> referencedPaths,
        DateTime orphanCutoffUtc,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(_basePath))
            return Task.FromResult(0);
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(path), ".registry-quota.lock", StringComparison.Ordinal))
                continue;
            var relative = Path.GetRelativePath(_basePath, path);
            var orphan = Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal)
                         || !referencedPaths.Contains(relative);
            if (!orphan || File.GetLastWriteTimeUtc(path) >= orphanCutoffUtc)
                continue;
            File.Delete(path);
            deleted++;
        }
        return Task.FromResult(deleted);
    }

    private async Task<FileStream> AcquireQuotaLeaseAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_basePath);
        var path = Path.Combine(_basePath, ".registry-quota.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> ExistingContentMatchesAsync(
        string fullPath,
        long expectedSize,
        byte[] expectedSha256,
        CancellationToken ct)
    {
        var file = new FileInfo(fullPath);
        if (file.Length != expectedSize)
            return false;
        await using var existing = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualSha256 = await SHA256.HashDataAsync(existing, ct).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actualSha256, expectedSha256);
    }

    private string SafeResolvePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_basePath, relativePath));
        var basePrefix = Path.GetFullPath(_basePath).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Package registry path traversal detected.");
        return fullPath;
    }
}
