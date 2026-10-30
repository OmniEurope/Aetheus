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

    /// <summary>R-58: the observability packages client projects restore while building their deploy image,
    /// on an agent without a token. A package row created by its first publication starts open to
    /// anonymous reading (the migration only opened rows that already existed); an administrator can close
    /// it afterwards, and every other package starts private.</summary>
    public static bool IsPublicByDefault(PackageRegistryKind kind, string normalizedName) => kind switch
    {
        PackageRegistryKind.NuGet => normalizedName is "aetheus.telemetry" or "aetheus.webanalytics",
        PackageRegistryKind.Npm => normalizedName == "@aetheus/web-analytics",
        _ => false
    };

    public static long ResolveMaxPackageBytes(IConfiguration configuration)
        => Math.Clamp(
            configuration.GetValue("PackageRegistry:MaxPackageBytes", DefaultMaxPackageBytes),
            1024 * 1024,
            MaximumPackageBytes);
}
