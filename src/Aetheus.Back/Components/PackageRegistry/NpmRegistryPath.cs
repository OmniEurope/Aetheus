// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.PackageRegistry;

/// <summary>
/// Reads the npm catch-all route: <c>{name}</c>, <c>{name}/{selector}</c> or the tarball
/// <c>{name}/-/{file}</c>, where a scoped name (<c>@scope/name</c>) spans two segments and may arrive
/// with an encoded slash. Shared by the controller and the anonymous read policy so both name the
/// same package.
/// </summary>
internal static class NpmRegistryPath
{
    public const string TarballMarker = "/-/";

    public static string Decode(string path)
        => Uri.UnescapeDataString(path.Trim('/'))
            .Replace("%2f", "/", StringComparison.OrdinalIgnoreCase);

    public static (string Name, string? Selector) SplitPackageAndSelector(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return (path, null);
        var nameLength = segments[0].StartsWith('@') ? 2 : 1;
        if (segments.Length <= nameLength)
            return (string.Join('/', segments), null);
        return (string.Join('/', segments.Take(nameLength)), string.Join('/', segments.Skip(nameLength)));
    }

    /// <summary>The package a raw route path reads (packument, version or tarball); null when empty.</summary>
    public static string? PackageName(string rawPath)
    {
        var decoded = Decode(rawPath);
        var tarballMarker = decoded.IndexOf(TarballMarker, StringComparison.Ordinal);
        var name = tarballMarker >= 0 ? decoded[..tarballMarker] : SplitPackageAndSelector(decoded).Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
