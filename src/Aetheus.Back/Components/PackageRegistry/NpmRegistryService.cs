// SPDX-License-Identifier: EUPL-1.2
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aetheus.Back.Data.Entities;
using NuGet.Versioning;

namespace Aetheus.Back.Components.PackageRegistry;

internal sealed class NpmRegistryService(
    IPackageRegistryRepository repository,
    IPackageRegistryStorage storage,
    NpmPackageInspector inspector,
    PackageRegistryPublishGate publishGate,
    IAuditService audit,
    IConfiguration configuration,
    IAdminChangeNotifier notifier) : INpmRegistryService
{
    private const int MaxStoredMetadataChars = 1024 * 1024;
    private readonly long _maxPackageBytes = PackageRegistryDefaults.ResolveMaxPackageBytes(configuration);

    public async Task PublishAsync(
        string routePackageName,
        JsonDocument publishDocument,
        string publishedBy,
        CancellationToken ct = default)
    {
        routePackageName = DecodePackageName(routePackageName);
        NpmPackageInspector.ValidatePackageName(routePackageName);
        var root = publishDocument.RootElement;
        var documentName = ReadRequiredString(root, "name");
        if (!string.Equals(routePackageName, documentName, StringComparison.Ordinal))
            throw new BadRequestException("The npm route and package document names do not match.");

        using var lease = await publishGate.EnterAsync(
            PackageRegistryKind.Npm, routePackageName, ct).ConfigureAwait(false);
        var package = await repository.GetPackageForUpdateAsync(
            PackageRegistryKind.Npm, routePackageName, ct).ConfigureAwait(false);

        var (versionName, versionDocument) = FindNewVersion(root, package);
        var semanticVersion = NpmPackageInspector.ParseVersion(versionName);
        var normalizedVersion = semanticVersion.ToNormalizedString().ToLowerInvariant();
        var temporaryTarball = await DecodeAttachmentToTemporaryFileAsync(root, ct).ConfigureAwait(false);
        StoredPackagePayload payload;
        bool isPrerelease;
        try
        {
            NpmArchiveMetadata archive;
            await using (var inspectionStream = File.OpenRead(temporaryTarball))
                archive = await inspector.InspectAsync(inspectionStream, ct).ConfigureAwait(false);
            isPrerelease = archive.IsPrerelease;
            if (!string.Equals(archive.Name, routePackageName, StringComparison.Ordinal)
                || archive.NormalizedVersion != normalizedVersion)
                throw new BadRequestException(
                    "The npm package document does not match package/package.json in the tarball.");

            await using var content = File.OpenRead(temporaryTarball);
            payload = await storage.SaveAsync(
                PackageRegistryKind.Npm,
                routePackageName,
                normalizedVersion,
                ".tgz",
                content,
                ct).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temporaryTarball);
        }

        var description = root.TryGetProperty("description", out var descriptionElement)
            && descriptionElement.ValueKind == JsonValueKind.String
            ? Truncate(descriptionElement.GetString(), 1000)
            : null;
        package ??= new RegistryPackage
        {
            Kind = PackageRegistryKind.Npm,
            Name = routePackageName,
            NormalizedName = routePackageName,
            Description = description
        };
        if (package.Id == 0)
            await repository.AddPackageAsync(package, ct).ConfigureAwait(false);
        else
            package.Description = description;

        package.DistTagsJson = ReadAndValidateDistTags(root, package, normalizedVersion, versionName);
        package.Versions.Add(new RegistryPackageVersion
        {
            Version = versionName,
            NormalizedVersion = normalizedVersion,
            FilePath = payload.RelativePath,
            ContentType = PackageRegistryDefaults.NpmContentType,
            SizeBytes = payload.SizeBytes,
            Sha256 = payload.Sha256,
            Sha1 = payload.Sha1,
            Integrity = payload.Integrity,
            Metadata = ReadBoundedMetadata(versionDocument),
            IsPrerelease = isPrerelease,
            PublishedBy = publishedBy
        });

        await PackageRegistryPublishFinalizer.SaveAndNotifyAsync(
            repository,
            storage,
            audit,
            notifier,
            payload,
            package,
            "NpmPackage",
            $"{routePackageName}@{versionName}",
            ct).ConfigureAwait(false);
    }

    public async Task<JsonObject?> GetPackumentAsync(
        string packageName, string registryBaseUrl, CancellationToken ct = default)
    {
        packageName = DecodePackageName(packageName);
        NpmPackageInspector.ValidatePackageName(packageName);
        var package = await repository.GetPackageAsync(
            PackageRegistryKind.Npm, packageName, ct).ConfigureAwait(false);
        return package is null || package.Versions.All(version => !version.IsListed)
            ? null
            : BuildPackument(package, registryBaseUrl);
    }

    public async Task<JsonObject?> GetVersionAsync(
        string packageName, string selector, string registryBaseUrl, CancellationToken ct = default)
    {
        packageName = DecodePackageName(packageName);
        var package = await repository.GetPackageAsync(
            PackageRegistryKind.Npm, packageName, ct).ConfigureAwait(false);
        if (package is null)
            return null;

        var selectedVersion = ResolveSelector(package, selector);
        return selectedVersion is null ? null : BuildVersion(package, selectedVersion, registryBaseUrl);
    }

    public async Task<RegistryPackageVersion?> GetTarballAsync(
        string packageName, string fileName, CancellationToken ct = default)
    {
        packageName = DecodePackageName(packageName);
        var versions = await repository.GetVersionNamesAsync(
            PackageRegistryKind.Npm, packageName, ct).ConfigureAwait(false);
        if (versions is null)
            return null;
        var baseName = packageName[(packageName.LastIndexOf('/') + 1)..];
        var versionName = versions.SingleOrDefault(version =>
            string.Equals(fileName, $"{baseName}-{version}.tgz", StringComparison.Ordinal));
        if (versionName is null)
            return null;
        var normalizedVersion = NpmPackageInspector.ParseVersion(versionName)
            .ToNormalizedString().ToLowerInvariant();
        var version = await repository.GetVersionAsync(
            PackageRegistryKind.Npm, packageName, normalizedVersion, ct).ConfigureAwait(false);
        return version?.IsListed == true ? version : null;
    }

    public async Task<NpmSearchPage> SearchAsync(
        string? query, int from, int size, CancellationToken ct = default)
    {
        var (packages, total) = await repository.SearchAsync(
            PackageRegistryKind.Npm,
            query,
            Math.Max(0, from),
            Math.Clamp(size, 1, 200),
            includePrerelease: true,
            ct).ConfigureAwait(false);
        var results = packages.Select(package =>
        {
            var latest = ResolveSelector(package, "latest")
                ?? package.Versions.Where(version => version.IsListed)
                    .OrderByDescending(version => version.CreatedAt).First();
            return new NpmSearchPackage(package.Name, latest.Version, package.Description, latest.CreatedAt);
        }).ToList();
        return new NpmSearchPage(total, results);
    }

    public Stream? OpenContent(RegistryPackageVersion version) => storage.OpenRead(version.FilePath);

    private static JsonObject BuildPackument(RegistryPackage package, string registryBaseUrl)
    {
        var versions = new JsonObject();
        var time = new JsonObject
        {
            ["created"] = package.CreatedAt,
            ["modified"] = package.UpdatedAt
        };
        foreach (var version in package.Versions.Where(item => item.IsListed))
        {
            versions[version.Version] = BuildVersion(package, version, registryBaseUrl);
            time[version.Version] = version.CreatedAt;
        }

        return new JsonObject
        {
            ["_id"] = package.Name,
            ["name"] = package.Name,
            ["description"] = package.Description,
            ["dist-tags"] = BuildVisibleDistTags(package),
            ["versions"] = versions,
            ["time"] = time,
            ["modified"] = package.UpdatedAt
        };
    }

    private static JsonObject BuildVersion(
        RegistryPackage package, RegistryPackageVersion version, string registryBaseUrl)
    {
        var document = JsonNode.Parse(version.Metadata)?.AsObject()
            ?? throw new InvalidOperationException("Stored npm metadata is invalid.");
        var baseName = package.Name[(package.Name.LastIndexOf('/') + 1)..];
        var packagePath = string.Join('/', package.Name.Split('/').Select(Uri.EscapeDataString));
        document["name"] = package.Name;
        document["version"] = version.Version;
        document["dist"] = new JsonObject
        {
            ["shasum"] = version.Sha1,
            ["integrity"] = version.Integrity,
            ["tarball"] = $"{registryBaseUrl.TrimEnd('/')}/{packagePath}/-/{Uri.EscapeDataString(baseName + "-" + version.Version + ".tgz")}"
        };
        if (version.IsDeprecated)
            document["deprecated"] = PackageRegistryDefaults.DeprecationMessage;
        return document;
    }

    private static RegistryPackageVersion? ResolveSelector(RegistryPackage package, string selector) =>
        ResolveSelector(
            package.DistTagsJson, package.Versions, selector,
            static version => version.IsListed,
            static version => version.NormalizedVersion);

    private static PackageRegistrySearchVersion? ResolveSelector(
        PackageRegistrySearchPackage package,
        string selector)
        => ResolveSelector(
            package.DistTagsJson, package.Versions, selector,
            static version => version.IsListed,
            static version => version.NormalizedVersion);

    private static T? ResolveSelector<T>(
        string? distTagsJson,
        IEnumerable<T> versions,
        string selector,
        Func<T, bool> isListed,
        Func<T, string> getNormalizedVersion)
        where T : class
    {
        var normalized = ResolveNormalizedSelector(distTagsJson, selector);
        return normalized is null
            ? null
            : versions.SingleOrDefault(version =>
                isListed(version) && getNormalizedVersion(version) == normalized);
    }

    private static string? ResolveNormalizedSelector(string? distTagsJson, string selector)
    {
        var tags = ParseObject(distTagsJson);
        if (tags[selector]?.GetValue<string>() is { } taggedVersion)
            selector = taggedVersion;
        return NuGetVersion.TryParse(selector, out var parsed)
            ? parsed.ToNormalizedString().ToLowerInvariant()
            : null;
    }

    private static (string Version, JsonElement Document) FindNewVersion(
        JsonElement root, RegistryPackage? existingPackage)
    {
        if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object)
            throw new BadRequestException("The npm publish document has no versions object.");
        var existing = existingPackage?.Versions.Select(version => version.NormalizedVersion)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var candidates = versions.EnumerateObject()
            .Where(property => !existing.Contains(
                NpmPackageInspector.ParseVersion(property.Name).ToNormalizedString().ToLowerInvariant()))
            .ToList();
        if (candidates.Count == 0)
            throw new ConflictException("This npm package version already exists.");
        if (candidates.Count != 1 || candidates[0].Value.ValueKind != JsonValueKind.Object)
            throw new BadRequestException("An npm publish request must introduce exactly one package version.");
        return (candidates[0].Name, candidates[0].Value);
    }

    private async Task<string> DecodeAttachmentToTemporaryFileAsync(
        JsonElement root,
        CancellationToken ct)
    {
        if (!root.TryGetProperty("_attachments", out var attachments)
            || attachments.ValueKind != JsonValueKind.Object)
            throw new BadRequestException("The npm publish document has no attachment.");
        var items = attachments.EnumerateObject().ToList();
        if (items.Count != 1
            || !items[0].Value.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.String)
            throw new BadRequestException("An npm publish request must contain exactly one base64 attachment.");
        var encoded = data.GetString()!;
        if (encoded.Length > ((_maxPackageBytes + 2) / 3 * 4))
            throw new BadRequestException($"Package exceeds the {_maxPackageBytes} byte registry limit.");

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"aetheus-npm-{Guid.NewGuid():N}.tgz");
        var buffer = ArrayPool<byte>.Shared.Rent(48 * 1024);
        try
        {
            await using var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long written = 0;
            const int encodedChunkSize = 64 * 1024;
            for (var offset = 0; offset < encoded.Length; offset += encodedChunkSize)
            {
                var count = Math.Min(encodedChunkSize, encoded.Length - offset);
                if (!Convert.TryFromBase64Chars(encoded.AsSpan(offset, count), buffer, out var decoded))
                    throw new BadRequestException("The npm attachment is not valid base64.");
                written += decoded;
                if (written > _maxPackageBytes)
                    throw new BadRequestException($"Package exceeds the {_maxPackageBytes} byte registry limit.");
                await output.WriteAsync(buffer.AsMemory(0, decoded), ct).ConfigureAwait(false);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            return temporaryPath;
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ReadAndValidateDistTags(
        JsonElement root, RegistryPackage package, string newNormalizedVersion, string newVersion)
    {
        var available = package.Versions.Select(version => version.NormalizedVersion)
            .Append(newNormalizedVersion)
            .ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var existingTag in ParseObject(package.DistTagsJson))
        {
            if (existingTag.Value?.GetValue<string>() is { } existingValue)
                result[existingTag.Key] = existingValue;
        }

        if (!root.TryGetProperty("dist-tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            result["latest"] = newVersion;
            return JsonSerializer.Serialize(result);
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if (tag.Name.Length is < 1 or > 64 || tag.Value.ValueKind != JsonValueKind.String)
                throw new BadRequestException("Invalid npm distribution tag.");
            var value = tag.Value.GetString()!;
            var normalized = NpmPackageInspector.ParseVersion(value).ToNormalizedString().ToLowerInvariant();
            if (!available.Contains(normalized))
                throw new BadRequestException("An npm distribution tag points to an unknown version.");
            result[tag.Name] = value;
        }
        return JsonSerializer.Serialize(result);
    }

    private static string ReadBoundedMetadata(JsonElement metadata)
    {
        var json = metadata.GetRawText();
        if (json.Length > MaxStoredMetadataChars)
            throw new BadRequestException($"npm version metadata exceeds {MaxStoredMetadataChars} characters.");
        return json;
    }

    private static JsonObject ParseObject(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException("Stored npm distribution tags are invalid.");

    private static JsonObject BuildVisibleDistTags(RegistryPackage package)
    {
        var listed = package.Versions
            .Where(version => version.IsListed)
            .ToDictionary(version => version.NormalizedVersion, StringComparer.Ordinal);
        var result = new JsonObject();
        foreach (var tag in ParseObject(package.DistTagsJson))
        {
            if (tag.Value?.GetValue<string>() is not { } value
                || !NuGetVersion.TryParse(value, out var parsed)
                || !listed.ContainsKey(parsed.ToNormalizedString().ToLowerInvariant()))
                continue;
            result[tag.Key] = value;
        }

        if (result["latest"] is null && listed.Count > 0)
        {
            result["latest"] = listed.Values
                .OrderByDescending(
                    version => NpmPackageInspector.ParseVersion(version.Version),
                    VersionComparer.VersionRelease)
                .First()
                .Version;
        }
        return result;
    }

    private static string DecodePackageName(string value)
        => Uri.UnescapeDataString(value.Trim('/')).Replace("%2f", "/", StringComparison.OrdinalIgnoreCase);

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new BadRequestException($"The npm publish document is missing '{propertyName}'.");
        return value.GetString()!;
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, maxLength)];
}
