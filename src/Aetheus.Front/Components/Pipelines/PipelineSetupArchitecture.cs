// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R1-18 / R1-31 / R1-33: what each <see cref="PipelineArchitecture"/> proposes on the wizard's
/// capabilities step (by the option names the wizard's address carries) and what it tells the
/// project's adapters through <c>APPLICATION_HAS_*</c>.
/// </summary>
public static class PipelineSetupArchitecture
{
    private static readonly HashSet<string> ApplicationCapabilities = new(StringComparer.Ordinal)
    {
        "ci", "quality", "security", "qa", "qa-extended", "security-history", "candidate", "nightly",
        "production", "fast-deploy", "rollback", "packages"
    };

    /// <summary>A package is built, checked and published: no candidate, deployment, rollback, QA of a
    /// running application or nightly. The package pipelines are the repository's existing package set.</summary>
    private static readonly HashSet<string> PackageCapabilities = new(StringComparer.Ordinal)
    {
        "ci", "quality", "security", "packages"
    };

    /// <summary>
    /// An external repository gets only what needs no file in it (recette R1-33): every other template
    /// calls an adapter under <c>.pipeline/scripts/</c> or reads <c>.gitleaks.toml</c>, which Aetheus
    /// does not write into a repository it does not own.
    /// </summary>
    private static readonly HashSet<string> ExternalCapabilities = new(StringComparer.Ordinal)
    {
        "external-security"
    };

    public static bool Proposes(PipelineArchitecture? architecture, string capability) => architecture switch
    {
        PipelineArchitecture.Application => ApplicationCapabilities.Contains(capability),
        PipelineArchitecture.Package => PackageCapabilities.Contains(capability),
        PipelineArchitecture.External => ExternalCapabilities.Contains(capability),
        _ => false
    };

    /// <summary>
    /// The <c>APPLICATION_HAS_FRONTEND</c>, <c>_BACKEND</c> and <c>_DATABASE</c> values. An application
    /// whose interface is served by its server project is one server project: no separate frontend is
    /// claimed. A package or an external application has none of them.
    /// </summary>
    public static (bool Frontend, bool Backend, bool Database) Parts(
        PipelineArchitecture? architecture, bool? sameProject, bool frontend, bool backend, bool database) =>
        (architecture, sameProject) switch
        {
            (PipelineArchitecture.Application, true) => (false, true, database),
            (PipelineArchitecture.Application, false) => (frontend, backend, database),
            _ => (false, false, false)
        };

    /// <summary>Whether the detail asked under the type is answered enough to go on.</summary>
    public static bool IsComplete(PipelineArchitecture? architecture, bool? sameProject, bool frontend, bool backend) =>
        architecture switch
        {
            null => false,
            PipelineArchitecture.Application => sameProject switch
            {
                null => false,
                true => true,
                false => frontend || backend
            },
            _ => true
        };

    /// <summary>The type's name in the wizard's address.</summary>
    public static string Slug(PipelineArchitecture architecture) => architecture switch
    {
        PipelineArchitecture.Application => "application",
        PipelineArchitecture.Package => "package",
        _ => "external"
    };

    public static PipelineArchitecture? FromSlug(string? slug) =>
        Enum.GetValues<PipelineArchitecture>()
            .Cast<PipelineArchitecture?>()
            .FirstOrDefault(value => string.Equals(Slug(value!.Value), slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>The resource keys of the type's title and description.</summary>
    public static (string Title, string Description) LabelKeys(PipelineArchitecture architecture) => architecture switch
    {
        PipelineArchitecture.Application => ("PipelineArchitectureApplication", "PipelineArchitectureApplicationHint"),
        PipelineArchitecture.Package => ("PipelineArchitecturePackage", "PipelineArchitecturePackageHint"),
        _ => ("PipelineArchitectureExternal", "PipelineArchitectureExternalHint")
    };
}
