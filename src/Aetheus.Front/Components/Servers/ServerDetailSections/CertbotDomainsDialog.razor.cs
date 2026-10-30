// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class CertbotDomainsDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public IReadOnlyList<string> Domains { get; set; } = [];
}
