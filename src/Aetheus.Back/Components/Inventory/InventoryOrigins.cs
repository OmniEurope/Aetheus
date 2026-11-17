// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Inventory;

/// <summary>
/// What an inventory item's <c>SourcePath</c> can hold besides a file of the repository: the origin it was read from. An SBOM component
/// and an image reported by a server name where they come from, never a file a person could edit.
/// </summary>
internal static class InventoryOrigins
{
    /// <summary>The origin of every component read from an SBOM report.</summary>
    public const string Sbom = "sbom";

    /// <summary>The prefix of the origin of an image a server reports (<c>server:{name}</c>).</summary>
    public const string ServerPrefix = "server:";

    /// <summary>True when the path names a file of the repository: not empty, not the SBOM, not a server.</summary>
    public static bool IsFile(string? sourcePath) =>
        !string.IsNullOrWhiteSpace(sourcePath)
        && !string.Equals(sourcePath.Trim(), Sbom, StringComparison.Ordinal)
        && !sourcePath.TrimStart().StartsWith(ServerPrefix, StringComparison.Ordinal);

    /// <summary>
    /// A NuGet package that is a .NET runtime or framework pack (<c>Microsoft.NETCore.App.Runtime.linux-x64</c>,
    /// <c>Microsoft.AspNetCore.App.Runtime.*</c>, <c>Microsoft.WindowsDesktop.App.Runtime.*</c>): it reaches a project through the SDK or the
    /// base image, never through a <c>PackageReference</c> to add.
    /// </summary>
    public static bool IsRuntimePack(string packageName) =>
        packageName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
        && packageName.Contains(".Runtime", StringComparison.OrdinalIgnoreCase);
}
