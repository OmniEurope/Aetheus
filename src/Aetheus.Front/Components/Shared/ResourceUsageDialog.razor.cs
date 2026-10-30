// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

public partial class ResourceUsageDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public IReadOnlyList<ResourceUsageUserDto> Users { get; set; } = [];
}
