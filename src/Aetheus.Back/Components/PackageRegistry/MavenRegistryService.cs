// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Aetheus.Back.Components.Shared.Maven;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PackageRegistry;

internal sealed class MavenRegistryService(
    IPackageRegistryRepository repository,
    IPackageRegistryStorage storage,
    PackageRegistryPublishGate publishGate,
    IAuditService audit,
    IAdminChangeNotifier notifier) : IMavenRegistryService
{
    private const int MaxMetadataBytes = 1024 * 1024;
    private const int MaxChecksumBytes = 1024;
    private const string ChecksumContentType = "text/plain";
    private const string XmlContentType = "application/xml";

    public async Task PutAsync(
        MavenRepositoryPath path, Stream content, string publishedBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Kind != MavenPathKind.File)
        {
            // The registry writes maven-metadata.xml itself from what it stores; the client's copy is
            // only checked for well-formedness and then dropped, together with its checksums.
            var bytes = await ReadBoundedAsync(content, path.Checksum == MavenChecksum.None ? MaxMetadataBytes : MaxChecksumBytes, ct)
                .ConfigureAwait(false);
            if (path.Checksum == MavenChecksum.None)
                EnsureWellFormed(bytes);
            return;
        }

        if (path.Checksum != MavenChecksum.None)
        {
            await VerifyUploadedChecksumAsync(path, content, ct).ConfigureAwait(false);
            return;
        }

        await PublishFileAsync(path, content, publishedBy, ct).ConfigureAwait(false);
    }

    public async Task<MavenRepositoryContent?> GetAsync(MavenRepositoryPath path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var package = await repository.GetPackageWithFilesAsync(
            PackageRegistryKind.Maven, path.Coordinates, forUpdate: false, ct).ConfigureAwait(false);
        if (package is null)
            return null;

        return path.Kind switch
        {
            MavenPathKind.ArtifactMetadata => Generated(path, ArtifactMetadata(path, package)),
            MavenPathKind.SnapshotMetadata => Generated(path, SnapshotMetadata(path, package)),
            _ => StoredFile(path, package)
        };
    }

    public Stream? OpenStored(string storagePath) => storage.OpenRead(storagePath);

    private async Task PublishFileAsync(
        MavenRepositoryPath path, Stream content, string publishedBy, CancellationToken ct)
    {
        var version = path.Version!;
        var isPom = path.Classifier is null && path.Extension == "pom";
        MavenPom? pom = null;
        if (isPom)
        {
            var pomBytes = await ReadBoundedAsync(content, MavenPomInspector.MaxPomBytes, ct).ConfigureAwait(false);
            pom = MavenPomInspector.Inspect(pomBytes, path);
            content = new MemoryStream(pomBytes, writable: false);
        }

        using var lease = await publishGate.EnterAsync(PackageRegistryKind.Maven, path.Coordinates, ct)
            .ConfigureAwait(false);
        var package = await repository.GetPackageWithFilesAsync(
            PackageRegistryKind.Maven, path.Coordinates, forUpdate: true, ct).ConfigureAwait(false);
        var packageVersion = package?.Versions.SingleOrDefault(item => item.NormalizedVersion == version);
        var existing = packageVersion?.Files.SingleOrDefault(file => file.FileName == path.FileName);

        var payload = await storage.SaveFileAsync(
            PackageRegistryKind.Maven, path.Coordinates, version, path.FileName, content, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Same bytes again (a retried upload): storage already proved the identity. Different
            // bytes never get here, storage refuses them as a conflict.
            return;
        }

        package ??= new RegistryPackage
        {
            Kind = PackageRegistryKind.Maven,
            Name = path.Coordinates,
            NormalizedName = path.Coordinates
        };
        if (package.Id == 0)
            await repository.AddPackageAsync(package, ct).ConfigureAwait(false);
        if (pom?.Description is not null)
            package.Description = pom.Description;

        if (packageVersion is null)
        {
            packageVersion = new RegistryPackageVersion
            {
                Version = version,
                NormalizedVersion = version,
                Metadata = JsonSerializer.Serialize(new { groupId = path.GroupId, artifactId = path.ArtifactId }),
                IsPrerelease = MavenSnapshot.IsSnapshot(version),
                PublishedBy = publishedBy
            };
            package.Versions.Add(packageVersion);
        }

        packageVersion.Files.Add(new RegistryPackageFile
        {
            FileName = path.FileName,
            FilePath = payload.RelativePath,
            ContentType = ContentTypeFor(path.Extension!),
            SizeBytes = payload.SizeBytes,
            Sha256 = payload.Sha256,
            Sha1 = payload.Sha1,
            Md5 = payload.Md5,
            Sha512 = payload.Sha512Hex,
            PublishedBy = publishedBy
        });
        DescribeVersionByPrimaryFile(packageVersion, path.ArtifactId);

        await PackageRegistryPublishFinalizer.SaveAndNotifyAsync(
            repository, storage, audit, notifier, payload, package,
            "MavenPackage", $"{path.Coordinates}:{version} {path.FileName}", ct).ConfigureAwait(false);
    }

    /// <summary>A version row describes one file, the one the admin page shows a digest for: the main
    /// artifact (no classifier, not the POM, not a signature) of the newest build, else its POM, else
    /// any file. Its size is the total of all its files.</summary>
    private static void DescribeVersionByPrimaryFile(RegistryPackageVersion version, string artifactId)
    {
        var primary = version.Files
            .Select(file => (File: file, Name: ParseStoredName(file.FileName, artifactId, version.Version)))
            .OrderBy(item => Rank(item.Name))
            .ThenByDescending(item => item.Name?.Build?.BuildNumber ?? 0)
            .ThenByDescending(item => item.File.Id == 0)
            .ThenByDescending(item => item.File.CreatedAt)
            .First().File;
        version.FilePath = primary.FilePath;
        version.ContentType = primary.ContentType;
        version.Sha256 = primary.Sha256;
        version.Sha1 = primary.Sha1;
        version.Integrity = "sha512-" + Convert.ToBase64String(Convert.FromHexString(primary.Sha512));
        version.SizeBytes = version.Files.Sum(file => file.SizeBytes);
    }

    private static int Rank(MavenRepositoryPath? name) => name switch
    {
        null => 3,
        { Classifier: null, Extension: "pom" } => 1,
        { Classifier: null } when !name.Extension!.EndsWith(".asc", StringComparison.Ordinal) => 0,
        _ => 2
    };

    private async Task VerifyUploadedChecksumAsync(MavenRepositoryPath path, Stream content, CancellationToken ct)
    {
        var bytes = await ReadBoundedAsync(content, MaxChecksumBytes, ct).ConfigureAwait(false);
        var declared = Encoding.ASCII.GetString(bytes).Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        var package = await repository.GetPackageWithFilesAsync(
            PackageRegistryKind.Maven, path.Coordinates, forUpdate: false, ct).ConfigureAwait(false);
        var file = FindFile(package, path)
                   ?? throw new NotFoundException($"No stored file {path.FileName} to verify the checksum against.");
        var expected = StoredChecksum(file, path.Checksum);
        if (!string.Equals(declared, expected, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException(
                $"Checksum mismatch for {path.FileName}: the registry stored {expected}.");
    }

    private static MavenRepositoryContent? StoredFile(MavenRepositoryPath path, RegistryPackage package)
    {
        var file = FindFile(package, path);
        if (file is null)
            return null;
        return path.Checksum == MavenChecksum.None
            ? new MavenRepositoryContent(file.ContentType, null, file.FilePath)
            : new MavenRepositoryContent(ChecksumContentType, Encoding.ASCII.GetBytes(StoredChecksum(file, path.Checksum)), null);
    }

    private static RegistryPackageFile? FindFile(RegistryPackage? package, MavenRepositoryPath path)
        => package?.Versions
            .SingleOrDefault(version => version.NormalizedVersion == path.Version)?
            .Files.SingleOrDefault(file => file.FileName == path.FileName);

    private static byte[]? ArtifactMetadata(MavenRepositoryPath path, RegistryPackage package)
    {
        var listed = package.Versions.Where(version => version.IsListed && version.Files.Count > 0).ToList();
        if (listed.Count == 0)
            return null;
        return MavenMetadata.WriteArtifact(
            path.GroupId,
            path.ArtifactId,
            listed.Select(version => version.Version),
            listed.SelectMany(version => version.Files).Max(file => file.CreatedAt));
    }

    private static byte[]? SnapshotMetadata(MavenRepositoryPath path, RegistryPackage package)
    {
        var version = package.Versions.SingleOrDefault(item => item.NormalizedVersion == path.Version);
        if (version is null || version.Files.Count == 0)
            return null;
        var files = version.Files
            .Select(file => (File: file, Name: ParseStoredName(file.FileName, path.ArtifactId, version.Version)))
            .Where(item => item.Name?.Build is not null)
            .Select(item => new SnapshotFile(item.Name!.Classifier, item.Name.Extension!, item.Name.Build, item.File.CreatedAt))
            .ToList();
        return files.Count == 0
            ? null
            : MavenMetadata.WriteSnapshot(path.GroupId, path.ArtifactId, version.Version, files,
                version.Files.Max(file => file.CreatedAt));
    }

    private static MavenRepositoryContent? Generated(MavenRepositoryPath path, byte[]? document)
    {
        if (document is null)
            return null;
        return path.Checksum == MavenChecksum.None
            ? new MavenRepositoryContent(XmlContentType, document, null)
            : new MavenRepositoryContent(ChecksumContentType, Encoding.ASCII.GetBytes(Hash(document, path.Checksum)), null);
    }

    /// <summary>Stored names were validated on the way in; reading them back through the same parser
    /// keeps a single definition of classifier, extension and snapshot build.</summary>
    private static MavenRepositoryPath? ParseStoredName(string fileName, string artifactId, string version)
        => MavenRepositoryPath.TryParse($"g/{artifactId}/{version}/{fileName}", out var result, out _)
            ? result
            : null;

    private static string StoredChecksum(RegistryPackageFile file, MavenChecksum checksum) => checksum switch
    {
        MavenChecksum.Md5 => file.Md5,
        MavenChecksum.Sha1 => file.Sha1,
        MavenChecksum.Sha256 => file.Sha256,
        MavenChecksum.Sha512 => file.Sha512,
        _ => throw new ArgumentOutOfRangeException(nameof(checksum))
    };

    private static string Hash(byte[] content, MavenChecksum checksum)
    {
        // MD5 and SHA-1 are what the Maven checksum files are named after; they identify bytes for the
        // client, they protect nothing (the registry's own integrity check is SHA-256).
        using var hash = IncrementalHash.CreateHash(checksum switch
        {
            MavenChecksum.Md5 => HashAlgorithmName.MD5,
            MavenChecksum.Sha1 => HashAlgorithmName.SHA1,
            MavenChecksum.Sha256 => HashAlgorithmName.SHA256,
            MavenChecksum.Sha512 => HashAlgorithmName.SHA512,
            _ => throw new ArgumentOutOfRangeException(nameof(checksum))
        });
        hash.AppendData(content);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string ContentTypeFor(string extension) => extension switch
    {
        "jar" or "war" or "ear" or "aar" => "application/java-archive",
        "pom" or "xml" => XmlContentType,
        _ when extension.EndsWith(".asc", StringComparison.Ordinal) => "application/pgp-signature",
        _ => "application/octet-stream"
    };

    private static void EnsureWellFormed(byte[] bytes)
    {
        try
        {
            SafeXml.Load(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
        }
        catch (XmlException ex)
        {
            throw new BadRequestException($"maven-metadata.xml is not well-formed XML: {ex.Message}");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream content, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new BadRequestException($"The uploaded file exceeds the {maxBytes} byte limit for its kind.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
