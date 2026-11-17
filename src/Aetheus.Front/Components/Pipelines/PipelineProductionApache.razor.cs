// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-38: says which Apache templates the production preparation creates in the project's configuration repository,
/// and, when the Compose file could not be read, asks whether to create them (the strategy is the one chosen on the Capabilities step).</summary>
public partial class PipelineProductionApache
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineSetupProduction Setup { get; set; } = default!;

    // The check box validates the expression it is given; the bound state lives in the setup object.
    private bool CreateValue => Setup.CreateApacheTemplates;
    private string StrategyLabel => Setup.Strategy == ProductionStrategy.BlueGreen ? L["PipelineSetupProdBlueGreen"].Value : L["PipelineSetupProdReplace"].Value;
}
