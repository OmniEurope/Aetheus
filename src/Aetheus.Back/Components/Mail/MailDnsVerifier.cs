// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

public interface IMailDnsVerifier
{
    /// <summary>Checks MX, SPF, DKIM and DMARC of <paramref name="domain"/> and updates its
    /// <c>HasSpf</c> / <c>HasDkim</c> / <c>HasDmarc</c> flags and <c>DnsCheckedAt</c> (caller saves).</summary>
    Task<MailDnsCheckDto> VerifyAsync(MailDomain domain, string? hostname, CancellationToken ct = default);

    /// <summary>The same four checks, read-only: the domain is left untouched (recette R2-090 status list).</summary>
    Task<IReadOnlyList<MailDnsRecordCheckDto>> CheckAsync(MailDomain domain, string? hostname, CancellationToken ct = default);
}

/// <summary>
/// PLAN-005 lot 5: DNS verification from the backend (the mail server is not necessarily a trustworthy
/// resolver, the control plane already has outbound DNS). Details are stable codes the UI localises:
/// <c>multiple-records</c>, <c>permissive-all</c>, <c>no-sender-mechanism</c>, <c>key-mismatch</c>,
/// <c>key-not-compared</c>, <c>wrong-target</c>, <c>resolver-error</c>.
/// </summary>
public sealed class MailDnsVerifier(IMailDnsResolver resolver, TimeProvider timeProvider) : IMailDnsVerifier
{
    private static readonly string[] s_senderMechanisms = ["mx", "a", "ip4:", "ip6:", "include:", "a:", "mx:"];

    public async Task<MailDnsCheckDto> VerifyAsync(MailDomain domain, string? hostname, CancellationToken ct = default)
    {
        var records = await CheckAsync(domain, hostname, ct).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        domain.HasSpf = records.Single(r => r.Kind == MailDnsRecordKind.Spf).Verdict == MailCheckVerdict.Ok;
        domain.HasDkim = records.Single(r => r.Kind == MailDnsRecordKind.Dkim).Verdict == MailCheckVerdict.Ok;
        domain.HasDmarc = records.Single(r => r.Kind == MailDnsRecordKind.Dmarc).Verdict == MailCheckVerdict.Ok;
        domain.DnsCheckedAt = now;
        return new MailDnsCheckDto { DomainId = domain.Id, Domain = domain.Name, CheckedAt = now, Records = [.. records] };
    }

    public async Task<IReadOnlyList<MailDnsRecordCheckDto>> CheckAsync(MailDomain domain, string? hostname, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return new List<MailDnsRecordCheckDto>
        {
            await CheckAsync(MailDnsRecordKind.Mx, domain.Name, MailDnsRecordFactory.MxHost(domain, hostname),
                () => resolver.MxAsync(domain.Name, ct), (observed, expected) => EvaluateMx(observed, expected)).ConfigureAwait(false),
            await CheckAsync(MailDnsRecordKind.Spf, domain.Name, MailDnsRecordFactory.SpfRecord,
                () => resolver.TxtAsync(domain.Name, ct), (observed, _) => EvaluateSpf(observed)).ConfigureAwait(false),
            await CheckAsync(MailDnsRecordKind.Dkim, MailDnsRecordFactory.DkimRecordName(domain), domain.DkimPublicKey,
                () => resolver.TxtAsync(MailDnsRecordFactory.DkimRecordName(domain), ct), EvaluateDkim).ConfigureAwait(false),
            await CheckAsync(MailDnsRecordKind.Dmarc, $"_dmarc.{domain.Name}", MailDnsRecordFactory.DmarcRecord(domain),
                () => resolver.TxtAsync($"_dmarc.{domain.Name}", ct), (observed, _) => EvaluateDmarc(observed)).ConfigureAwait(false)
        };
    }

    private static async Task<MailDnsRecordCheckDto> CheckAsync(
        MailDnsRecordKind kind,
        string name,
        string expected,
        Func<Task<IReadOnlyList<string>>> lookup,
        Func<IReadOnlyList<string>, string, (MailCheckVerdict Verdict, string Detail, IReadOnlyList<string> Relevant)> evaluate)
    {
        try
        {
            var observed = await lookup().ConfigureAwait(false);
            var (verdict, detail, relevant) = evaluate(observed, expected);
            return new MailDnsRecordCheckDto
            {
                Kind = kind,
                Name = name,
                Expected = expected,
                Observed = [.. relevant],
                Verdict = verdict,
                Detail = detail
            };
        }
        catch (MailDnsLookupException)
        {
            return new MailDnsRecordCheckDto
            {
                Kind = kind,
                Name = name,
                Expected = expected,
                Verdict = MailCheckVerdict.Unknown,
                Detail = "resolver-error"
            };
        }
    }

    internal static (MailCheckVerdict, string, IReadOnlyList<string>) EvaluateMx(IReadOnlyList<string> hosts, string expected)
    {
        if (hosts.Count == 0) return (MailCheckVerdict.Missing, string.Empty, hosts);
        return hosts.Contains(expected, StringComparer.OrdinalIgnoreCase)
            ? (MailCheckVerdict.Ok, string.Empty, hosts)
            : (MailCheckVerdict.Mismatch, "wrong-target", hosts);
    }

    internal static (MailCheckVerdict, string, IReadOnlyList<string>) EvaluateSpf(IReadOnlyList<string> txt)
    {
        var spf = txt.Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (spf.Count == 0) return (MailCheckVerdict.Missing, string.Empty, spf);
        if (spf.Count > 1) return (MailCheckVerdict.Mismatch, "multiple-records", spf);
        var terms = spf[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(t => t.TrimStart('+').ToLowerInvariant()).ToList();
        if (spf[0].Contains("+all", StringComparison.OrdinalIgnoreCase))
            return (MailCheckVerdict.Mismatch, "permissive-all", spf);
        return terms.Any(t => s_senderMechanisms.Any(m => m.EndsWith(':') ? t.StartsWith(m, StringComparison.Ordinal) : t == m))
            ? (MailCheckVerdict.Ok, string.Empty, spf)
            : (MailCheckVerdict.Mismatch, "no-sender-mechanism", spf);
    }

    internal static (MailCheckVerdict, string, IReadOnlyList<string>) EvaluateDkim(IReadOnlyList<string> txt, string expectedTxt)
    {
        var dkim = txt.Where(t => t.Contains("p=", StringComparison.Ordinal)).ToList();
        if (dkim.Count == 0) return (MailCheckVerdict.Missing, string.Empty, dkim);
        if (dkim.Count > 1) return (MailCheckVerdict.Mismatch, "multiple-records", dkim);
        var expectedKey = MailTaskOutputParser.DkimPublicKeyOf(expectedTxt);
        if (expectedKey.Length == 0) return (MailCheckVerdict.Ok, "key-not-compared", dkim);
        return MailTaskOutputParser.DkimPublicKeyOf(dkim[0]) == expectedKey
            ? (MailCheckVerdict.Ok, string.Empty, dkim)
            : (MailCheckVerdict.Mismatch, "key-mismatch", dkim);
    }

    internal static (MailCheckVerdict, string, IReadOnlyList<string>) EvaluateDmarc(IReadOnlyList<string> txt)
    {
        var dmarc = txt.Where(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList();
        return dmarc.Count switch
        {
            0 => (MailCheckVerdict.Missing, string.Empty, dmarc),
            1 => (MailCheckVerdict.Ok, string.Empty, dmarc),
            _ => (MailCheckVerdict.Mismatch, "multiple-records", dmarc)
        };
    }
}
