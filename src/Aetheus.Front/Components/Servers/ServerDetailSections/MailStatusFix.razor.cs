// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>The fix of one red line of the mail status, chosen from its key.</summary>
public partial class MailStatusFix
{
    internal enum FixKind { Services, Certificate, Dns, Unknown }

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public MailDiagnosticItemDto Item { get; set; } = new();

    /// <summary>The mail host name the agent reports (Postfix <c>myhostname</c>).</summary>
    [Parameter] public string Hostname { get; set; } = string.Empty;

    [Parameter] public bool CanManage { get; set; }
    // Each button is busy from its click until the task it queued completes; while one certificate
    // task runs, the other certificate button waits (disabled).
    [Parameter] public bool StartServicesBusy { get; set; }
    [Parameter] public bool ObtainCertificateBusy { get; set; }
    [Parameter] public bool InstallCertificateBusy { get; set; }
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

    /// <summary>The record kind of the line: <c>mx</c>, <c>spf</c>, <c>dkim</c>, <c>dmarc</c>, or the server key.</summary>
    private string Prefix => Item.Key.IndexOf(':', StringComparison.Ordinal) is var colon and > 0 ? Item.Key[..colon] : Item.Key;

    private string Domain => Item.Key.IndexOf(':', StringComparison.Ordinal) is var colon and > 0 ? Item.Key[(colon + 1)..] : string.Empty;

    /// <summary>What the record (or the check) is, and why mail needs it, in plain words.</summary>
    internal string? WhatItIs => Prefix switch
    {
        "hostname-dns" => L["MailFixWhatA"].Value,
        "reverse-dns" => L["MailFixWhatPtr"].Value,
        "tls" => L["MailFixWhatTls"].Value,
        "mx" => L["MailFixWhatMx"].Value,
        "spf" => L["MailFixWhatSpf"].Value,
        "dkim" => L["MailFixWhatDkim"].Value,
        "dmarc" => L["MailFixWhatDmarc"].Value,
        _ => null
    };

    /// <summary>What publishing the record changes, written under the record to copy.</summary>
    internal string? Effect => Kind != FixKind.Dns ? null : Prefix switch
    {
        "mx" => L["MailFixEffectMx"].Value,
        "spf" => L["MailFixEffectSpf"].Value,
        "dkim" => L["MailFixEffectDkim"].Value,
        "dmarc" => Format(L["MailFixEffectDmarc"], Domain),
        _ => null
    };

    /// <summary> An MX that still shows another host may be an old answer kept in DNS caches until
    /// its time-to-live runs out. With the remaining TTL the resolver reported, the note says how
    /// long; without it, the note says so in general terms. Nothing is estimated. </summary>
    internal string? CacheNote
    {
        get
        {
            if (Prefix != "mx" || Item.Verdict != MailCheckVerdict.Mismatch) return null;
            return Item.TimeToLiveSeconds is { } seconds and >= 0
                ? Format(L["MailFixMxCacheTtl"], Duration(TimeSpan.FromSeconds(seconds)))
                : L["MailFixMxCacheUnknown"];
        }
    }

    private string Duration(TimeSpan remaining) => remaining.TotalHours >= 1
        ? string.Format(CultureInfo.CurrentCulture, L["MailTtlHoursMinutes"], (int)remaining.TotalHours, remaining.Minutes)
        : Format(L["MailTtlMinutes"], Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)).ToString(CultureInfo.CurrentCulture));

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
