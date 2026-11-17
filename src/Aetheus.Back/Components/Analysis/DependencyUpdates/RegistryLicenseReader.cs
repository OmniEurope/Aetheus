// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>The SPDX license expression a package registry gives a package version, or why there is none. Never both, never a guess.</summary>
internal sealed record RegistryLicenseAnswer(string? Expression, string? Failure)
{
    public static RegistryLicenseAnswer Found(string expression) => new(expression, null);
    public static RegistryLicenseAnswer Failed(string reason) => new(null, reason);
}

/// <summary>
/// The license of the exact version an update moves to, as the registry that publishes it says (recette R-31 for NuGet, R-8 for npm
/// and Packagist). One reader per ecosystem, named by <see cref="Ecosystem"/>; an SBOM is never read for a target version's license.
/// </summary>
internal interface IRegistryLicenseReader
{
    /// <summary>The ecosystem name of the watch matches this reader answers for (<c>NuGet</c>, <c>npm</c>, <c>Packagist</c>).</summary>
    string Ecosystem { get; }

    Task<RegistryLicenseAnswer> ReadAsync(string packageId, string version, CancellationToken ct);
}
