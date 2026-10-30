// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunDeployedTile
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;
}
