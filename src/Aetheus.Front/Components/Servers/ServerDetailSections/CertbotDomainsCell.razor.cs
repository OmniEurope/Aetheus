// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class CertbotDomainsCell
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    /// <summary>The certificate whose names the cell shows.</summary>
    [Parameter, EditorRequired] public CertbotCertificateDto Certificate { get; set; } = default!;

    private IReadOnlyList<string> Domains => Certificate.Domains;

    private string CountLabel => string.Format(L["CertbotDomainsBadgeLabel"], Domains.Count);

    /// <summary>Two names or more: the full list opens in a dialog.</summary>
    private Task OpenListAsync() =>
        Dialog.OpenAsync<CertbotDomainsDialog>(
            string.Format(L["CertbotDomainsDialogTitle"], Certificate.Name),
            new Dictionary<string, object?> { [nameof(CertbotDomainsDialog.Domains)] = Domains });
}
