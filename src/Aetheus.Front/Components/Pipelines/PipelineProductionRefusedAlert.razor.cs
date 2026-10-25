// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>The warning of the production card when the environment sample of the configuration repository still names
/// a variable the agent refuses (recette R-5), with the one action that mends it, then its success notice.</summary>
public partial class PipelineProductionRefusedAlert
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineSetupProduction Setup { get; set; } = default!;

    private Task RepairAsync() => Setup.RepairAsync();
}
