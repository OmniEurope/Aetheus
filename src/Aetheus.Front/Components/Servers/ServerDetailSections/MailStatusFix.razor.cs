// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Recette R2-090: the fix of one red line of the mail status, chosen from its key.</summary>
public partial class MailStatusFix
{
    internal enum FixKind { Services, Certificate, Dns, Unknown }

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public MailDiagnosticItemDto Item { get; set; } = new();

    /// <summary>The mail host name the agent reports (Postfix <c>myhostname</c>).</summary>
    [Parameter] public string Hostname { get; set; } = string.Empty;

    [Parameter] public bool CanManage { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public EventCallback OnStartServices { get; set; }
    [Parameter] public EventCallback OnObtainCertificate { get; set; }
    [Parameter] public EventCallback OnInstallCertificate { get; set; }

    private bool HostnameKnown => MailValidation.IsValidDomainName(Hostname);

    internal FixKind Kind => Item.Key switch
    {
        _ when Item.Verdict == MailCheckVerdict.Unknown => FixKind.Unknown,
        "services" => FixKind.Services,
        "tls" => FixKind.Certificate,
        _ when Item.RecordType.Length > 0 => FixKind.Dns,
        _ => FixKind.Unknown
    };

    private bool ShowsRecord => Kind == FixKind.Dns && Item.RecordValue.Length > 0;

    private string Explanation => Kind switch
    {
        FixKind.Services => Format(L["MailFixServices"], Item.Detail),
        FixKind.Certificate => HostnameKnown ? Format(L["MailFixCertificate"], Hostname) : L["MailFixCertificateNoHost"],
        FixKind.Dns => DnsExplanation,
        _ => L["MailFixUnknown"]
    };

    private string DnsExplanation => Item.Key switch
    {
        "hostname-dns" => L["MailFixARecord"],
        "reverse-dns" => L["MailFixReverseDns"],
        _ when Item.Key.StartsWith("mx:", StringComparison.Ordinal) => L["MailFixMx"],
        _ when Item.Key.StartsWith("dkim:", StringComparison.Ordinal) && Item.RecordValue.Length == 0 => L["MailFixDkimUnknown"],
        _ => L["MailFixTxt"]
    };

    private static string Format(string format, string value) => string.Format(CultureInfo.CurrentCulture, format, value);
}
