// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

public interface IMailDnsVerifier
{
    /// <summary>Checks MX, SPF, DKIM and DMARC of <paramref name="domain"/> and updates its
    /// <c>HasSpf</c> / <c>HasDkim</c> / <c>HasDmarc</c> flags and <c>DnsCheckedAt</c> (caller saves).
    /// <paramref name="serverAddress"/> is the public address of the mail server, the one SPF must authorise.</summary>
    Task<MailDnsCheckDto> VerifyAsync(MailDomain domain, string? hostname, string? serverAddress, CancellationToken ct = default);

    /// <summary>Checks DNS without modifying the domain.</summary>
    Task<IReadOnlyList<MailDnsRecordCheckDto>> CheckAsync(MailDomain domain, string? hostname, string? serverAddress, CancellationToken ct = default);
}

/// <summary> DNS verification from the backend (the mail server is not necessarily a trustworthy
/// resolver, the control plane already has outbound DNS). Details are stable codes the UI
/// localises: <c>multiple-records</c>, <c>permissive-all</c>, <c>no-sender-mechanism</c>,
/// <c>server-not-authorized</c>, <c>no-address</c>, <c>key-mismatch</c>, <c>key-not-compared</c>,
/// <c>wrong-target</c>, <c>resolver-error</c>. </summary>
public sealed class MailDnsVerifier(IMailDnsResolver resolver, TimeProvider timeProvider) : IMailDnsVerifier
{
    private static readonly string[] s_senderMechanisms = ["mx", "a", "ip4", "ip6", "include"];

    private readonly MailSpfAuthorization _spf = new(resolver);

    public async Task<MailDnsCheckDto> VerifyAsync(MailDomain domain, string? hostname, string? serverAddress, CancellationToken ct = default)
    {
        var records = await CheckAsync(domain, hostname, serverAddress, ct).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        domain.HasSpf = records.Single(r => r.Kind == MailDnsRecordKind.Spf).Verdict == MailCheckVerdict.Ok;
        domain.HasDkim = records.Single(r => r.Kind == MailDnsRecordKind.Dkim).Verdict == MailCheckVerdict.Ok;
        domain.HasDmarc = records.Single(r => r.Kind == MailDnsRecordKind.Dmarc).Verdict == MailCheckVerdict.Ok;
        domain.DnsCheckedAt = now;
        return new MailDnsCheckDto { DomainId = domain.Id, Domain = domain.Name, CheckedAt = now, Records = [.. records] };
    }

    public async Task<IReadOnlyList<MailDnsRecordCheckDto>> CheckAsync(MailDomain domain, string? hostname, string? serverAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var mx = await CheckAsync(MailDnsRecordKind.Mx, domain.Name, MailDnsRecordFactory.MxHost(domain, hostname),
            () => resolver.MxAsync(domain.Name, ct), (observed, expected) => Task.FromResult(EvaluateMx(observed, expected))).ConfigureAwait(false);
        return new List<MailDnsRecordCheckDto>
        {
            await WithMxTimeToLiveAsync(mx, ct).ConfigureAwait(false),
            await CheckAsync(MailDnsRecordKind.Spf, domain.Name, MailDnsRecordFactory.SpfRecord,
                () => resolver.TxtAsync(domain.Name, ct), (observed, _) => EvaluateSpfAsync(domain.Name, observed, serverAddress, ct)).ConfigureAwait(false),
            await CheckAsync(MailDnsRecordKind.Dkim, MailDnsRecordFactory.DkimRecordName(domain), domain.DkimPublicKey,
                () => resolver.TxtAsync(MailDnsRecordFactory.DkimRecordName(domain), ct),
                (observed, expected) => Task.FromResult(EvaluateDkim(observed, expected))).ConfigureAwait(false),
            await CheckAsync(MailDnsRecordKind.Dmarc, $"_dmarc.{domain.Name}", MailDnsRecordFactory.DmarcRecord(domain),
                () => resolver.TxtAsync($"_dmarc.{domain.Name}", ct), (observed, _) => Task.FromResult(EvaluateDmarc(observed))).ConfigureAwait(false)
        };
    }

    /// <summary>An MX that still shows another host carries the remaining time-to-live of the
    /// answer seen, as the resolver reports it, so the page can say how long a cached old answer
    /// may last. No TTL is invented: a resolver that does not give one leaves it null.</summary>
    private async Task<MailDnsRecordCheckDto> WithMxTimeToLiveAsync(MailDnsRecordCheckDto mx, CancellationToken ct)
    {
        if (mx.Verdict != MailCheckVerdict.Mismatch) return mx;
        try
        {
            return mx with { TimeToLiveSeconds = await resolver.MxTimeToLiveAsync(mx.Name, ct).ConfigureAwait(false) };
        }
        catch (MailDnsLookupException)
        {
            return mx;
        }
    }

    private static async Task<MailDnsRecordCheckDto> CheckAsync(
        MailDnsRecordKind kind,
        string name,
        string expected,
        Func<Task<IReadOnlyList<string>>> lookup,
        Func<IReadOnlyList<string>, string, Task<(MailCheckVerdict Verdict, string Detail, IReadOnlyList<string> Relevant)>> evaluate)
    {
        try
        {
            var observed = await lookup().ConfigureAwait(false);
            var (verdict, detail, relevant) = await evaluate(observed, expected).ConfigureAwait(false);
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

    /// <summary>SPF must authorize this mail server through the proposed mx/a terms or a
    /// receiver-equivalent pass from <see cref="MailSpfAuthorization"/>. Merely authorizing another
    /// provider is insufficient.</summary>
    internal async Task<(MailCheckVerdict, string, IReadOnlyList<string>)> EvaluateSpfAsync(
        string domain, IReadOnlyList<string> txt, string? serverAddress, CancellationToken ct)
    {
        var spf = txt.Where(IsSpf).ToList();
        if (spf.Count == 0) return (MailCheckVerdict.Missing, string.Empty, spf);
        if (spf.Count > 1) return (MailCheckVerdict.Mismatch, "multiple-records", spf);
        var terms = Terms(spf[0]);
        if (terms.Any(t => t is "all" or "+all"))
            return (MailCheckVerdict.Mismatch, "permissive-all", spf);
        if (!terms.Any(IsSenderTerm))
            return (MailCheckVerdict.Mismatch, "no-sender-mechanism", spf);
        if (MailDnsRecordFactory.IsProposedSpf(spf[0]))
            return (MailCheckVerdict.Ok, string.Empty, spf);
        if (!IPAddress.TryParse(serverAddress, out var address))
            return (MailCheckVerdict.Unknown, "no-address", spf);
        return await _spf.AuthorizesAsync(domain, spf[0], address, ct).ConfigureAwait(false)
            ? (MailCheckVerdict.Ok, string.Empty, spf)
            : (MailCheckVerdict.Mismatch, "server-not-authorized", spf);
    }

    internal static bool IsSpf(string txt) =>
        txt.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase) || txt.Equals("v=spf1", StringComparison.OrdinalIgnoreCase);

    internal static List<string> Terms(string spf) =>
        [.. spf.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(t => t.ToLowerInvariant())];

    private static bool IsSenderTerm(string term)
    {
        if (term.StartsWith("redirect=", StringComparison.Ordinal)) return true;
        var bare = term.TrimStart('+');
        var end = bare.IndexOfAny([':', '/']);
        return s_senderMechanisms.Contains(end < 0 ? bare : bare[..end], StringComparer.Ordinal);
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
