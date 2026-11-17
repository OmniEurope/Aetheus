// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Aetheus.Back.Components.VulnerabilityWatch;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Inventory;

/// <summary>One revision read. <see cref="Result"/> is null when its manifests are those already known.</summary>
internal sealed record RepositoryInventory(string CommitSha, string Fingerprint, InventoryBuildResult? Result);

/// <summary>
/// Reads the manifests of one revision, one at a time and within their limits. A manifest that cannot be
/// read makes the inventory partial and says why; it never reads as a package-free file.
/// </summary>
internal sealed class RepositoryInventoryReader(
    IProjectSourceReader source,
    IOptions<VulnerabilityWatchOptions> options,
    ILogger<RepositoryInventoryReader> logger)
{
    /// <summary>Raised when a parser reads more than before, so the next pass reads again what it skipped.</summary>
    private const string ParserVersion = "2";

    private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];

    private readonly VulnerabilityWatchOptions _options = options.Value;

    public async Task<RepositoryInventory?> ReadAsync(int repositoryId, string revision, string? knownFingerprint, CancellationToken ct)
    {
        var tree = await source.ReadTreeAsync(repositoryId, revision, ct).ConfigureAwait(false);
        if (tree is null) return null;
        var manifests = tree.Files
            .Select(file => (file, kind: ManifestCatalog.Classify(file.Path)))
            .Where(item => item.kind is not null)
            .Select(item => new ManifestFile(item.file.Path, item.file.BlobId, item.kind!.Value))
            .OrderBy(manifest => manifest.Path, StringComparer.Ordinal)
            .ToList();
        var fingerprint = Fingerprint(manifests);
        if (string.Equals(fingerprint, knownFingerprint, StringComparison.Ordinal))
            return new RepositoryInventory(tree.CommitSha, fingerprint, null);

        var collector = new ManifestCollector(manifests);
        var plan = collector.Plan();
        foreach (var manifest in plan.Take(_options.MaxManifestFiles))
            await ReadManifestAsync(repositoryId, tree.CommitSha, manifest, collector, ct).ConfigureAwait(false);
        if (plan.Count > _options.MaxManifestFiles) collector.Gaps.Add(new InventoryGap(InventoryGapCodes.FileLimit));
        if (manifests.Count == 0) collector.Gaps.Add(new InventoryGap(InventoryGapCodes.NoManifest));

        var result = InventoryBuilder.Build(collector.Entries, collector.Gaps, collector.DirectNames, _options.MaxInventoryItems);
        return new RepositoryInventory(tree.CommitSha, fingerprint, result);
    }

    private async Task ReadManifestAsync(
        int repositoryId, string commitSha, ManifestFile manifest, ManifestCollector collector, CancellationToken ct)
    {
        var stream = await source.OpenFileAsync(repositoryId, commitSha, manifest.Path, ct).ConfigureAwait(false);
        if (stream is null)
        {
            collector.Gaps.Add(new InventoryGap(InventoryGapCodes.Unreadable, manifest.Technology));
            return;
        }

        var content = await ReadBoundedAsync(stream, ct).ConfigureAwait(false);
        if (content is null)
        {
            collector.Gaps.Add(new InventoryGap(InventoryGapCodes.FileTooLarge, manifest.Technology));
            return;
        }

        try
        {
            collector.Read(manifest, content);
        }
        catch (Exception exception) when (exception is JsonException or XmlException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Inventory of repository {Repository}: {Path} cannot be read", repositoryId, manifest.Path);
            collector.Gaps.Add(new InventoryGap(InventoryGapCodes.Unreadable, manifest.Technology));
        }
    }

    /// <summary>Null when the file is over its limit. The byte order mark is dropped: the JSON reader refuses it.</summary>
    private async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _options.MaxManifestBytes) return null;
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            var content = buffer.ToArray();
            return content.AsSpan().StartsWith(ByteOrderMark) ? content[ByteOrderMark.Length..] : content;
        }
    }

    private static string Fingerprint(IEnumerable<ManifestFile> manifests)
    {
        var text = new StringBuilder(ParserVersion);
        foreach (var manifest in manifests) text.Append('\n').Append(manifest.Path).Append(' ').Append(manifest.BlobId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
