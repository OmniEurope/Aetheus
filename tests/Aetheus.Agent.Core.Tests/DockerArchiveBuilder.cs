// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Writes a small docker-archive (<c>docker save</c> layout: <c>manifest.json</c> plus one <c>layer.tar</c> per
/// layer) holding the given files, so the tests read a real tar-in-tar image rather than a double of the reader.</summary>
internal static class DockerArchiveBuilder
{
    /// <summary>A layer: files by image path, and the whiteouts it carries (<c>.wh.name</c> next to the file it removes).</summary>
    internal sealed record Layer(IReadOnlyDictionary<string, string> Files, bool Gzip = false);

    public static string Write(string path, params Layer[] layers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var archive = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: false);
        var names = new List<string>();
        for (var index = 0; index < layers.Length; index++)
        {
            var name = $"layer{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}/layer.tar";
            names.Add(name);
            AddFile(archive, name, LayerBytes(layers[index]));
        }
        var manifest = "[{\"Config\":\"config.json\",\"RepoTags\":[\"test:1\"],\"Layers\":["
            + string.Join(",", names.Select(name => $"\"{name}\"")) + "]}]";
        AddFile(archive, "manifest.json", Encoding.UTF8.GetBytes(manifest));
        return path;
    }

    private static byte[] LayerBytes(Layer layer)
    {
        using var buffer = new MemoryStream();
        using (var tar = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, content) in layer.Files)
                AddFile(tar, path, Encoding.UTF8.GetBytes(content));
        }
        if (!layer.Gzip)
            return buffer.ToArray();
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(buffer.ToArray());
        return compressed.ToArray();
    }

    private static void AddFile(TarWriter writer, string name, byte[] content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) };
        writer.WriteEntry(entry);
    }

    /// <summary>The <c>deps.json</c> of a framework or application that lists the given library keys
    /// (<c>Microsoft.NETCore.App.Runtime.linux-x64/10.0.13</c>), with the type Trivy and the host read.</summary>
    public static string Deps(params string[] libraries) =>
        "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0/linux-x64\"},\"libraries\":{"
        + string.Join(",", libraries.Select(library => $"\"{library}\":{{\"type\":\"{(library.StartsWith("runtimepack.", StringComparison.Ordinal) ? "runtimepack" : "package")}\",\"serviceable\":true,\"sha512\":\"\"}}"))
        + "}}";
}
