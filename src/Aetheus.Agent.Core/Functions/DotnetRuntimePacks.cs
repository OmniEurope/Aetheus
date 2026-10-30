// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>One .NET runtime pack a <c>*.deps.json</c> lists, e.g. <c>Microsoft.AspNetCore.App.Runtime.linux-x64</c>
/// 10.0.13, with the file of the image that lists it.</summary>
internal sealed record DotnetRuntimePack(string Package, DotnetVersion Version, string DepsPath);

/// <summary>Finds the .NET runtime inside the dependency manifests of an image. The shared framework of an
/// <c>aspnet</c> image lists its own runtime pack under <c>libraries</c> (<c>Microsoft.NETCore.App.Runtime.linux-x64/10.0.13</c>),
/// and a self-contained application lists it with a <c>runtimepack.</c> prefix: the same entries Trivy reads as
/// <c>dotnet-core</c> packages, which is why a vulnerability database that knows the runtime's CVEs reports them.</summary>
internal static class DotnetRuntimePacks
{
    private const string SelfContainedPrefix = "runtimepack.";

    private static readonly string[] Families =
    [
        "Microsoft.NETCore.App.Runtime.",
        "Microsoft.AspNetCore.App.Runtime.",
        "Microsoft.WindowsDesktop.App.Runtime."
    ];

    /// <summary>The runtime packs one manifest lists, in the order it lists them. A manifest that is not JSON, or
    /// that lists a runtime pack under a version that is not a version, is refused: reading it as "no runtime"
    /// would report nothing about an image nobody could read.</summary>
    public static IReadOnlyList<DotnetRuntimePack> Read(string depsPath, string depsJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(depsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{depsPath} is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("libraries", out var libraries)
                || libraries.ValueKind != JsonValueKind.Object)
                return [];

            var packs = new List<DotnetRuntimePack>();
            foreach (var library in libraries.EnumerateObject())
            {
                var key = library.Name.StartsWith(SelfContainedPrefix, StringComparison.Ordinal)
                    ? library.Name[SelfContainedPrefix.Length..]
                    : library.Name;
                var slash = key.LastIndexOf('/');
                if (slash <= 0 || !Families.Any(family => key.StartsWith(family, StringComparison.Ordinal)))
                    continue;
                var version = DotnetVersion.TryParse(key[(slash + 1)..])
                    ?? throw new InvalidDataException($"{depsPath} lists the runtime pack '{library.Name}' under a version that is not a .NET version.");
                packs.Add(new DotnetRuntimePack(key[..slash], version, depsPath));
            }
            return packs;
        }
    }
}
