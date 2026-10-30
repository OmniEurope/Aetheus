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

    /// <summary>True when the tile is offered: the candidate and the production deployment are both created.</summary>
    public static bool IsOffered(IReadOnlyCollection<string> createdSuffixes) =>
        createdSuffixes.Contains(PipelineSetupRoles.Candidate) && createdSuffixes.Contains(PipelineSetupRoles.DeployProd);
}
