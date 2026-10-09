// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// PLAN-005: turns the stable codes the backend and the agent emit (verdicts, diagnostic keys, detail
/// codes, collector findings <c>code|parameter</c>) into localised text, so no raw code reaches the user.
/// Unknown codes are shown verbatim rather than hidden.
/// </summary>
public static class MailUiText
{
    private static readonly Dictionary<string, string> s_diagnosticKeys = new(StringComparer.Ordinal)
    {
        ["installed"] = "MailDiagInstalled",
        ["capability"] = "MailDiagCapability",
        ["helper-version"] = "MailDiagHelperVersion",
        ["services"] = "MailDiagServices",
        ["spam-filter"] = "MailDiagSpamFilter",
        ["tls"] = "MailDiagTls",
        ["queue"] = "MailDiagQueue",
        ["hostname-dns"] = "MailDiagHostnameDns",
        ["reverse-dns"] = "MailDiagReverseDns"
    };

    private static readonly Dictionary<string, string> s_details = new(StringComparer.Ordinal)
    {
        ["resolver-error"] = "MailDetailResolverError",
        ["wrong-target"] = "MailDetailWrongTarget",
        ["multiple-records"] = "MailDetailMultipleRecords",
        ["permissive-all"] = "MailDetailPermissiveAll",
        ["no-sender-mechanism"] = "MailDetailNoSenderMechanism",
        ["key-mismatch"] = "MailDetailKeyMismatch",
        ["key-not-compared"] = "MailDetailKeyNotCompared",
        ["self-signed"] = "MailDetailSelfSigned",
        ["expiring"] = "MailDetailExpiring",
        ["expired"] = "MailDetailExpired",
        ["unreadable"] = "MailDetailUnreadable",
        ["stopped"] = "MailDetailStopped",
        ["no-hostname"] = "MailDetailNoHostname",
        ["no-address"] = "MailDetailNoAddress",
        ["enable-mail-setup"] = "MailDetailEnableMailSetup"
    };

    private static readonly Dictionary<string, string> s_findings = new(StringComparer.Ordinal)
    {
        ["unreadable"] = "MailFindingUnreadable",
        ["missing"] = "MailFindingMissing",
        ["unsupported-map"] = "MailFindingUnsupportedMap",
        ["truncated"] = "MailFindingTruncated",
        ["aliases-skipped"] = "MailFindingAliasesSkipped",
        ["tls-unreadable"] = "MailFindingTlsUnreadable",
        ["tls-invalid"] = "MailFindingTlsInvalid",
        ["dkim-public-unreadable"] = "MailFindingDkimPublicUnreadable"
    };

    public static OmniTone VerdictStyle(MailCheckVerdict verdict) => verdict switch
    {
        MailCheckVerdict.Ok => OmniTone.Success,
        MailCheckVerdict.Missing => OmniTone.Danger,
        MailCheckVerdict.Mismatch => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    public static string Verdict(IStringLocalizer<AppStrings> l, MailCheckVerdict verdict) => verdict switch
    {
        MailCheckVerdict.Ok => l["MailVerdictOk"],
        MailCheckVerdict.Missing => l["MailVerdictMissing"],
        MailCheckVerdict.Mismatch => l["MailVerdictMismatch"],
        _ => l["MailVerdictUnknown"]
    };

    /// <summary>Label of a diagnostics row; <c>mx:&lt;domain&gt;</c> rows carry the domain.</summary>
    public static string DiagnosticLabel(IStringLocalizer<AppStrings> l, string key)
    {
        // Recette R2-090: SPF, DKIM and DMARC lines carry their domain like the MX line.
        foreach (var (prefix, resource) in s_domainKeys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, l[resource], key[prefix.Length..]);
        }
        return s_diagnosticKeys.TryGetValue(key, out var label) ? l[label] : key;
    }

    private static readonly (string Prefix, string Resource)[] s_domainKeys =
    [
        ("mx:", "MailDiagMx"),
        ("spf:", "MailDiagSpf"),
        ("dkim:", "MailDiagDkim"),
        ("dmarc:", "MailDiagDmarc")
    ];

    /// <summary>Localised detail when the code is known, otherwise the raw detail (addresses, counts, host names).</summary>
    public static string Detail(IStringLocalizer<AppStrings> l, string detail) =>
        s_details.TryGetValue(detail, out var resource) ? l[resource] : detail;

    /// <summary>Collector finding <c>code|parameter</c> rendered as a sentence.</summary>
    public static string Finding(IStringLocalizer<AppStrings> l, string finding)
    {
        var separator = finding.IndexOf('|');
        var code = separator < 0 ? finding : finding[..separator];
        var parameter = separator < 0 ? string.Empty : finding[(separator + 1)..];
        return s_findings.TryGetValue(code, out var resource)
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, l[resource], parameter)
            : finding;
    }
}
