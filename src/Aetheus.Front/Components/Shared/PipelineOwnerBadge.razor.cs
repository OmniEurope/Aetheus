// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Warn when the pipeline owner cannot authorize its chained runs.</summary>
public partial class PipelineOwnerBadge
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public string? OwnerUsername { get; set; }

    [Parameter] public bool OwnerInactive { get; set; }
}
