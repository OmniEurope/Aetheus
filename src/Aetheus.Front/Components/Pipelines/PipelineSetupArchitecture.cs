// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary> What each <see cref="PipelineArchitecture"/> proposes on the wizard's capabilities
/// step (by the option names the wizard's address carries) and what it tells the project's adapters
/// through <c>APPLICATION_HAS_*</c>. </summary>
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

    /// <summary> An external repository gets only the platform's own scans: every other template runs
    /// build, test or deployment commands the project declares, or deploys what it builds, which
    /// Aetheus does not do for an application it does not own (recette R1-33). </summary>
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
    /// The <c>APPLICATION_HAS_FRONTEND</c>, <c>_BACKEND</c> and <c>_DATABASE</c> values. The Full-stack
    /// tile (<paramref name="fullStack"/> true) is one monolith serving the interface and the API: one
    /// server project, no separate frontend is claimed. Under the front-end and back-end tile
    /// (<paramref name="fullStack"/> false) each part is claimed as it was checked. A package or an
    /// external application has none of them.
    /// </summary>
    public static (bool Frontend, bool Backend, bool Database) Parts(
        PipelineArchitecture? architecture, bool? fullStack, bool frontend, bool backend, bool database) =>
        (architecture, fullStack) switch
        {
            (PipelineArchitecture.Application, true) => (false, true, database),
            (PipelineArchitecture.Application, false) => (frontend, backend, database),
            _ => (false, false, false)
        };

    /// <summary>Whether the detail asked under the type is answered enough to go on: an application
    /// needs one of its two tiles, and the separate one at least a front-end or a back-end.</summary>
    public static bool IsComplete(PipelineArchitecture? architecture, bool? fullStack, bool frontend, bool backend) =>
        architecture switch
        {
            null => false,
            PipelineArchitecture.Application => fullStack switch
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
