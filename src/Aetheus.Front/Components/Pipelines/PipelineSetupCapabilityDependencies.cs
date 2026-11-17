// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-23 (user decision 2026-11-10, option 2): the capabilities of the Capabilities step that only work with another one.
/// A dependent tile is greyed, with the visible mention "depends on ...", while its dependency is unticked; unticking a dependency
/// unticks the dependents it forces off. Nothing is ever ticked on the user's behalf. The dependencies are the ones the pipeline
/// templates and the wizard declare:
/// <list type="bullet">
/// <item><c>qa-extended</c> and <c>security-history</c> need <c>nightly</c>: their templates say "only the nightly triggers this
/// pipeline" (<c>application-qa-extended</c>, <c>application-security-history</c>), and the nightly names them in
/// <c>APPLICATION_QA_EXTENDED_PIPELINE</c> and <c>APPLICATION_SECURITY_HISTORY_PIPELINE</c>.</item>
/// <item><c>deploy-after-candidate</c> needs a candidate (<c>candidate</c>) and the production deployment (<c>production</c>): the
/// candidate starts <c>APPLICATION_DEPLOY_PIPELINE</c> (<see cref="PipelineSetupDeployChain.IsOffered(IReadOnlyCollection{string}, bool)"/>).</item>
/// <item><c>deploy-approval</c> needs <c>production</c>: it is written as <c>APPLICATION_DEPLOY_REQUIRE_APPROVAL</c> into the
/// production deployment only (<see cref="PipelineSetupDeployApproval.IsOffered"/>).</item>
/// </list>
/// </summary>
internal static class PipelineSetupCapabilityDependencies
{
    private static readonly IReadOnlyDictionary<string, string[]> Dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["qa-extended"] = ["nightly"],
        ["security-history"] = ["nightly"],
        ["deploy-after-candidate"] = ["candidate", "production"],
        ["deploy-approval"] = ["production"]
    };

    /// <summary>The capabilities <paramref name="capability"/> depends on, empty for one that depends on nothing.</summary>
    public static IReadOnlyList<string> Of(string capability) =>
        Dependencies.TryGetValue(capability, out var dependencies) ? dependencies : [];

    /// <summary>The capabilities that depend on <paramref name="capability"/>.</summary>
    public static IReadOnlyList<string> Dependents(string capability) =>
        [.. Dependencies.Where(item => item.Value.Contains(capability, StringComparer.Ordinal)).Select(item => item.Key)];

    /// <summary>The resource key of the title of a capability that others depend on, as its own tile shows it.</summary>
    public static string TitleKey(string capability) => capability switch
    {
        "nightly" => "Nightly",
        "candidate" => "Candidate",
        "production" => "ProductionDeployment",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "No tile of the Capabilities step is depended on by name.")
    };
}
