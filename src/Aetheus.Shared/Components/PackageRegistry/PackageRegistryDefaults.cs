// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.PackageRegistry;

/// <summary>Canonical public package-registry origins used by feed creation and resolution.</summary>
public static class PackageRegistryDefaults
{
    public const string NuGet = "https://api.nuget.org";
    public const string Npm = "https://registry.npmjs.org";
    public const string PyPi = "https://pypi.org";

    /// <summary>Maven Central. Unlike the other registries a Maven repository lives under a path,
    /// so this value is the repository root, not only an origin.</summary>
    public const string Maven = "https://repo.maven.apache.org/maven2";

    public static string For(PackageFeedType feedType) => feedType switch
    {
        PackageFeedType.NuGet => NuGet,
        PackageFeedType.Npm => Npm,
        PackageFeedType.PyPI => PyPi,
        PackageFeedType.Maven => Maven,
        _ => string.Empty
    };
}
