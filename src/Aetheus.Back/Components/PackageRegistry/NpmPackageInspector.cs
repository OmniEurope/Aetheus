// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace Aetheus.Back.Components.PackageRegistry;

internal sealed class NpmPackageInspector
{
    private const int MaxEntriesBeforeManifest = 4096;
    private const long MaxDecompressedBytesBeforeManifest = 64L * 1024 * 1024;
    private static readonly Regex UnscopedNamePattern = new(
        "^[a-z0-9][a-z0-9._~-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<NpmArchiveMetadata> InspectAsync(Stream content, CancellationToken ct)
    {
        try
        {
            return await InspectCoreAsync(content, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw new BadRequestException("The npm attachment is not a valid tar.gz archive.");
        }
        catch (JsonException)
        {
            throw new BadRequestException("The npm package.json is not valid JSON.");
        }
    }

    private static async Task<NpmArchiveMetadata> InspectCoreAsync(Stream content, CancellationToken ct)
    {
        if (content.CanSeek)
            content.Position = 0;

        using var gzip = new GZipStream(content, CompressionMode.Decompress, leaveOpen: true);
        using var bounded = new BoundedReadStream(gzip, MaxDecompressedBytesBeforeManifest, leaveOpen: true);
        using var tar = new TarReader(bounded, leaveOpen: true);
        TarEntry? entry;
        var entryCount = 0;
        while ((entry = await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false)) is not null)
        {
            if (++entryCount > MaxEntriesBeforeManifest)
                throw new BadRequestException("The npm archive contains too many entries before package.json.");
            var name = entry.Name.Replace('\\', '/');
            if (!string.Equals(name, "package/package.json", StringComparison.Ordinal))
                continue;
            if (entry.DataStream is null || entry.Length <= 0 || entry.Length > 1024 * 1024)
                throw new BadRequestException("The npm package.json is missing or exceeds 1 Mo.");

            using var document = await JsonDocument.ParseAsync(entry.DataStream, cancellationToken: ct)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var packageName = ReadRequiredString(root, "name");
            var packageVersion = ReadRequiredString(root, "version");
            ValidatePackageName(packageName);
            var version = ParseVersion(packageVersion);
            if (content.CanSeek)
                content.Position = 0;
            return new NpmArchiveMetadata(
                packageName,
                packageName.ToLowerInvariant(),
                packageVersion,
                version.ToNormalizedString().ToLowerInvariant(),
                version.IsPrerelease);
        }

        throw new BadRequestException("The npm tarball does not contain package/package.json.");
    }

    public static NuGetVersion ParseVersion(string value)
    {
        if (value.Length > 100 || !NuGetVersion.TryParse(value, out var version))
            throw new BadRequestException("Invalid npm semantic version.");
        return version;
    }

    public static void ValidatePackageName(string value)
    {
        if (value.Length is < 1 or > 214 || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
            throw new BadRequestException("Invalid npm package name.");

        if (value.StartsWith('@'))
        {
            var slash = value.IndexOf('/');
            if (slash <= 1 || slash == value.Length - 1
                || !UnscopedNamePattern.IsMatch(value[1..slash])
                || !UnscopedNamePattern.IsMatch(value[(slash + 1)..]))
                throw new BadRequestException("Invalid scoped npm package name.");
            return;
        }

        if (!UnscopedNamePattern.IsMatch(value))
            throw new BadRequestException("Invalid npm package name.");
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
            throw new BadRequestException($"npm package.json is missing '{propertyName}'.");
        return property.GetString()!;
    }
}

internal sealed record NpmArchiveMetadata(
    string Name,
    string NormalizedName,
    string Version,
    string NormalizedVersion,
    bool IsPrerelease);
