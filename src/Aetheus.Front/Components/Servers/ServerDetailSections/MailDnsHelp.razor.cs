// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Recette R1-11: the step-by-step help to publish the mail DNS records, for the first mail domain.</summary>
public partial class MailDnsHelp
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The mail domain the examples name (postmaster address, record names); a placeholder when unknown.</summary>
    [Parameter] public string Domain { get; set; } = string.Empty;

    private string ShownDomain => Domain.Length > 0 ? Domain : L["MailHelpYourDomain"];

    private string Format(string key) => string.Format(CultureInfo.CurrentCulture, L[key], ShownDomain);
}
