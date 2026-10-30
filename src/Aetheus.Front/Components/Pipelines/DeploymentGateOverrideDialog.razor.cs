// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-72: the confirmation of a deployment despite the quality gate. Closes with <c>true</c> to deploy,
/// <c>false</c> to go back.</summary>
public partial class DeploymentGateOverrideDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<DeploymentGateWarningDto> Warnings { get; set; } = [];

    private static OmniIconName IconOf(DeploymentGateWarningKind kind) => kind switch
    {
        DeploymentGateWarningKind.NoGrade or DeploymentGateWarningKind.NoVerdict => OmniIconName.Question,
        DeploymentGateWarningKind.Rejected => OmniIconName.Prohibit,
        DeploymentGateWarningKind.BlockingTests => OmniIconName.Bug,
        DeploymentGateWarningKind.SecurityFindings => OmniIconName.ShieldWarning,
        _ => OmniIconName.Warning
    };
}
