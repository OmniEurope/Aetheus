// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

/// <summary>
/// PLAN-005: the DNS records a mail domain needs, derived from the server inventory. Shared by the
/// "records to publish" dialog and the DNS verification so both always expect the same values.
/// </summary>
internal static class MailDnsRecordFactory
{
    public const string SpfRecord = "v=spf1 mx a ~all";

    /// <summary>MX target: the Postfix <c>myhostname</c> reported by the agent, else <c>mail.&lt;domain&gt;</c>.</summary>
    public static string MxHost(MailDomain domain, string? hostname) =>
        MailValidation.IsValidDomainName(hostname) ? hostname!.ToLowerInvariant() : $"mail.{domain.Name}";

    /// <summary>Recette R1-10: the record Aetheus proposes is published: it starts with <c>mx</c> and <c>a</c>
    /// (pass), alone (<see cref="SpfRecord"/>) or put before the terms already published (<see cref="SpfToPublish"/>).</summary>
    public static bool IsProposedSpf(string spf)
    {
        var terms = MailDnsVerifier.Terms(spf).Take(2).Select(t => t.TrimStart('+')).Order(StringComparer.Ordinal).ToList();
        return terms.SequenceEqual(["a", "mx"]);
    }

    /// <summary>
    /// Recette R1-10: the SPF record to publish when the one published does not authorise this server. A
    /// single published record keeps its terms (another provider that still sends for the domain stays
    /// authorised) and gets <c>mx a</c> in front; otherwise the plain <see cref="SpfRecord"/>.
    /// </summary>
    public static string SpfToPublish(MailDnsRecordCheckDto check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.Detail != "server-not-authorized" || check.Observed.Count != 1) return SpfRecord;
        var kept = MailDnsVerifier.Terms(check.Observed[0]).Where(t => t.TrimStart('+') is not ("a" or "mx")).ToList();
        if (!kept.Any(t => t.TrimStart('-', '~', '?') == "all" || t.StartsWith("redirect=", StringComparison.Ordinal)))
            kept.Add("~all");
        return string.Join(' ', ["v=spf1", "mx", "a", .. kept]);
    }

    public static string DkimRecordName(MailDomain domain) => $"{domain.DkimSelector}._domainkey.{domain.Name}";

    public static string DmarcRecord(MailDomain domain) => $"v=DMARC1; p=quarantine; rua=mailto:postmaster@{domain.Name}";

    public static MailDnsRecordsDto Build(MailDomain domain, string? hostname) => new()
    {
        Domain = domain.Name,
        MxRecord = $"10 {MxHost(domain, hostname)}.",
        SpfRecord = SpfRecord,
        DkimSelector = domain.DkimSelector,
        DkimRecord = DkimRecordName(domain),
        DkimPublicKey = domain.DkimPublicKey,
        DkimKeyStatus = string.IsNullOrEmpty(domain.DkimPublicKey) ? MailDkimKeyStatus.Reading : MailDkimKeyStatus.Available,
        DmarcRecord = DmarcRecord(domain)
    };
}
