// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>State badges for mail domains, accounts and aliases.</summary>
public partial class MailRecordBadges
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public bool IsActive { get; set; }
    [Parameter] public MailRecordSource Source { get; set; }
    [Parameter] public DateTime? MissingSince { get; set; }
}
