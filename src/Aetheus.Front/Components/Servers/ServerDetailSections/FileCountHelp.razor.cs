// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>A question mark explaining, in one sentence, why a disk can be full of small files with free space.</summary>
public partial class FileCountHelp
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private string Help => L["FileCountHelp"];
}
