// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.PackageRegistry;

internal static class PackageRegistryDefaults
{
    public const long DefaultMaxPackageBytes = 100L * 1024 * 1024;
    public const long MaximumPackageBytes = 100L * 1024 * 1024;
    public const long MaxRequestBodyBytes = 140L * 1024 * 1024;
    public const string NuGetContentType = "application/octet-stream";
    public const string NpmContentType = "application/octet-stream";

    /// <summary>Text served in the npm `deprecated` field and the NuGet `deprecation` entry of a deprecated version.</summary>
    public const string DeprecationMessage = "This version is deprecated.";

    public static long ResolveMaxPackageBytes(IConfiguration configuration)
        => Math.Clamp(
            configuration.GetValue("PackageRegistry:MaxPackageBytes", DefaultMaxPackageBytes),
            1024 * 1024,
            MaximumPackageBytes);
}
