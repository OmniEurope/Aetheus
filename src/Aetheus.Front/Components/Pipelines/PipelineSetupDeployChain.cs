// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-72 (user decision 2026-10-25): whether the candidate the wizard creates starts the deployment pipeline created in
/// the same pass. A tile of the wizard, ticked by default, shown only when both pipelines are created.
/// The wizard's address names the choice by what leaves the default (<see cref="SkipOption"/>, written when the tile is
/// unticked), so an address written before this option existed, which carries no such name, reopens ticked.
/// </summary>
internal sealed class PipelineSetupDeployChain
{
    /// <summary>The option's name in the wizard's address, present only when the tile is unticked.</summary>
    public const string SkipOption = "skip-deploy-after-candidate";

    public bool RunAfterCandidate { get; set; } = true;

    /// <summary>The choice as the wizard's address carries it: present when unticked, so absent means the default.</summary>
    public (string Name, Func<bool> Get, Action<bool> Set) Choice =>
        (SkipOption, () => !RunAfterCandidate, skipped => RunAfterCandidate = !skipped);

    /// <summary>R-36: the candidate and the production deployment are both created in this pass and the candidate starts it. The
    /// deployment is then given no server at launch, so the choice is made in the summary, before the creation.</summary>
    public bool NeedsServer(IReadOnlyCollection<string> createdSuffixes) => RunAfterCandidate && IsOffered(createdSuffixes);

    /// <summary>True when the tile is offered: the candidate is created and the production deployment is created too, or already
    /// held by the project (<paramref name="deployProdHeld"/>, R-36: a candidate created in a second pass is chained to it).</summary>
    public static bool IsOffered(IReadOnlyCollection<string> createdSuffixes, bool deployProdHeld = false) =>
        createdSuffixes.Contains(PipelineSetupRoles.Candidate)
        && (createdSuffixes.Contains(PipelineSetupRoles.DeployProd) || deployProdHeld);

    /// <summary><see cref="IsOffered(IReadOnlyCollection{string}, bool)"/> where the deployment pipeline is held when the project's
    /// <paramref name="existingPipelines"/> name <c>{prefix}-deploy-prod</c>.</summary>
    public static bool IsOffered(
        IReadOnlyCollection<string> createdSuffixes, IReadOnlySet<string> existingPipelines, string prefix) =>
        IsOffered(createdSuffixes, existingPipelines.Contains($"{prefix}-{PipelineSetupRoles.DeployProd}"));
}
