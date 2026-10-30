// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>The <c>*.deps.json</c> files an image ends up with, read from a docker-archive (<c>docker save</c>) without
/// unpacking it on disk. The layers are applied in the order <c>manifest.json</c> lists them, as the container runtime
/// does: a file a later layer deletes (a <c>.wh.</c> whiteout) or hides (an opaque directory) is not in the image, so a
/// runtime an image replaced in a later layer is not reported as the one it ships.</summary>
internal static class ContainerImageDepsFiles
{
    private const string DepsSuffix = ".deps.json";
    private const string WhiteoutPrefix = ".wh.";
    private const string OpaqueMarker = ".wh..wh..opq";
    private const long MaxDepsBytes = 32L * 1024 * 1024;

    /// <summary>Image path (no leading slash) to the file's text. An archive that is not a docker-archive, or that
    /// lacks a layer it lists, is refused with <see cref="InvalidDataException"/>.</summary>
    public static IReadOnlyDictionary<string, string> Read(string archivePath, CancellationToken cancellationToken)
    {
        var layerNames = ReadLayerNames(archivePath, cancellationToken);
        var layers = new Dictionary<string, LayerChanges>(StringComparer.Ordinal);
        using (var file = File.OpenRead(archivePath))
        using (var reader = new TarReader(file))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Normalize(entry.Name);
                if (IsFile(entry) && layerNames.Contains(name, StringComparer.Ordinal))
                    layers[name] = ReadLayer(entry.DataStream!, name, cancellationToken);
            }
        }

        var image = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layerName in layerNames)
        {
            if (!layers.TryGetValue(layerName, out var layer))
                throw new InvalidDataException($"The image archive lists the layer {layerName} but does not contain it.");
            layer.ApplyTo(image);
        }
        return image;
    }

    private static List<string> ReadLayerNames(string archivePath, CancellationToken cancellationToken)
    {
        using var file = File.OpenRead(archivePath);
        using var reader = new TarReader(file);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsFile(entry) || Normalize(entry.Name) != "manifest.json")
                continue;
            using var text = new StreamReader(entry.DataStream!, Encoding.UTF8);
            return ParseLayerNames(text.ReadToEnd());
        }
        throw new InvalidDataException("The image archive has no manifest.json: it is not a docker-archive.");
    }

    private static List<string> ParseLayerNames(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            var layers = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
                && document.RootElement[0].TryGetProperty("Layers", out var found) && found.ValueKind == JsonValueKind.Array
                    ? found.EnumerateArray().Select(layer => layer.GetString()).ToList()
                    : null;
            if (layers is null || layers.Count == 0 || layers.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("The manifest.json of the image archive lists no layers.");
            return [.. layers.Select(layer => Normalize(layer!))];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The manifest.json of the image archive is not valid JSON: {ex.Message}", ex);
        }
    }

    private static LayerChanges ReadLayer(Stream data, string layerName, CancellationToken cancellationToken)
    {
        var changes = new LayerChanges();
        var gzip = data.ReadByte() == 0x1f && data.ReadByte() == 0x8b;
        data.Seek(0, SeekOrigin.Begin);
        using var content = gzip ? new GZipStream(data, CompressionMode.Decompress, leaveOpen: true) : data;
        using var reader = new TarReader(content, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Normalize(entry.Name);
            var directory = path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;
            var file = path[(directory.Length == 0 ? 0 : directory.Length + 1)..];
            if (file == OpaqueMarker)
                changes.OpaqueDirectories.Add(directory);
            else if (file.StartsWith(WhiteoutPrefix, StringComparison.Ordinal))
                changes.Removed.Add(directory.Length == 0 ? file[WhiteoutPrefix.Length..] : directory + "/" + file[WhiteoutPrefix.Length..]);
            else if (IsFile(entry) && file.EndsWith(DepsSuffix, StringComparison.OrdinalIgnoreCase))
                changes.Added[path] = ReadText(entry, path, layerName);
        }
        return changes;
    }

    private static string ReadText(TarEntry entry, string path, string layerName)
    {
        if (entry.Length > MaxDepsBytes)
            throw new InvalidDataException($"{path} in layer {layerName} is {entry.Length} bytes, above the {MaxDepsBytes} bytes a dependency manifest may have.");
        using var text = new StreamReader(entry.DataStream!, Encoding.UTF8);
        return text.ReadToEnd();
    }

    private static bool IsFile(TarEntry entry) =>
        entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is not null;

    /// <summary>The path without a leading <c>./</c> or <c>/</c> and without a trailing <c>/</c>; a leading dot that
    /// belongs to the name (<c>.wh.</c>) is kept.</summary>
    private static string Normalize(string name)
    {
        var path = name.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith('/'))
            path = path[(path.StartsWith("./", StringComparison.Ordinal) ? 2 : 1)..];
        return path.TrimEnd('/');
    }

    /// <summary>What one layer adds to the image and what it takes away from the layers below it.</summary>
    private sealed class LayerChanges
    {
        public Dictionary<string, string> Added { get; } = new(StringComparer.Ordinal);
        public List<string> Removed { get; } = [];
        public List<string> OpaqueDirectories { get; } = [];

        public void ApplyTo(Dictionary<string, string> image)
        {
            foreach (var directory in OpaqueDirectories)
                RemoveUnder(image, directory, includeSelf: false);
            foreach (var removed in Removed)
                RemoveUnder(image, removed, includeSelf: true);
            foreach (var (path, text) in Added)
                image[path] = text;
        }

        private static void RemoveUnder(Dictionary<string, string> image, string path, bool includeSelf)
        {
            var prefix = path.Length == 0 ? string.Empty : path + "/";
            foreach (var existing in image.Keys.ToList())
            {
                if (existing.StartsWith(prefix, StringComparison.Ordinal) || (includeSelf && existing == path))
                    image.Remove(existing);
            }
        }
    }
}
