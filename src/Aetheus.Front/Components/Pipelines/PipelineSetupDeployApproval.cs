// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-60 (user decision 2026-11-03): whether the production deployment the wizard creates asks for an approval before it
/// deploys, even for a release graded A. A tile of the wizard, ticked by default, shown only when the deployment pipeline is
/// created; it is written into that pipeline as <c>APPLICATION_DEPLOY_REQUIRE_APPROVAL</c>, which the control plane turns into
/// the one approval stage of the deployment template. Who decides it is not a choice of the pipeline: the decision belongs to
/// the people the platform lets approve (the project's administrators, with the approval permission).
/// The wizard's address names the choice by what leaves the default (<see cref="SkipOption"/>, written when the tile is
/// unticked), so an address written before this option existed reopens ticked.
/// </summary>
internal sealed class PipelineSetupDeployApproval
{
    /// <summary>The option's name in the wizard's address, present only when the tile is unticked.</summary>
    public const string SkipOption = "skip-deploy-approval";

    public bool Ask { get; set; } = true;

    /// <summary>The choice as the wizard's address carries it: present when unticked, so absent means the default.</summary>
    public (string Name, Func<bool> Get, Action<bool> Set) Choice =>
        (SkipOption, () => !Ask, skipped => Ask = !skipped);

    /// <summary>True when the tile is offered: the deployment pipeline is created (one the project already holds is kept as it is).</summary>
    public static bool IsOffered(IReadOnlyCollection<string> createdSuffixes) =>
        createdSuffixes.Contains(PipelineSetupRoles.DeployProd);

    /// <summary>The step's parameters for this option: its value, its change, and whether it is offered.</summary>
    public void AddAttributes(
        RenderTreeBuilder builder, object receiver, Action refresh, IReadOnlyCollection<string> createdSuffixes)
    {
        builder.AddAttribute(230, nameof(PipelineSetupWizardStep.DeployApproval), Ask);
        builder.AddAttribute(231, nameof(PipelineSetupWizardStep.DeployApprovalChanged),
            EventCallback.Factory.Create<bool>(receiver, value =>
            {
                Ask = value;
                refresh();
            }));
        builder.AddAttribute(232, nameof(PipelineSetupWizardStep.DeployApprovalOffered), IsOffered(createdSuffixes));
    }
}
