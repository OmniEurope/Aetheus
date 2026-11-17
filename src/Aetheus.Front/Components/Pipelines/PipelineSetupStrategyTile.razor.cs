// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The production strategy of the capabilities step (R-26): two exclusive tiles, the replacement of the services (proposed
/// by default) and blue-green. A monolith has the replacement only (R-11), so the choice is fixed.</summary>
public partial class PipelineSetupStrategyTile
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The strategy chosen, one of <see cref="ProductionStrategy"/>.</summary>
    [Parameter] public string Value { get; set; } = ProductionStrategy.Replace;

    [Parameter] public EventCallback<string> StrategyChanged { get; set; }

    /// <summary>The choice cannot be changed (a monolith is deployed by replacement only).</summary>
    [Parameter] public bool Fixed { get; set; }
}
