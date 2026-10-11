// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using DnsClient;
using DnsClient.Protocol;

namespace Aetheus.Back.Components.Mail;

/// <summary>DnsClient-backed <see cref="IMailDnsResolver"/> using the host's configured name servers.</summary>
public sealed class DnsClientMailResolver : IMailDnsResolver
{
    private readonly LookupClient _client = new(new LookupClientOptions
    {
        UseCache = true,
        Timeout = TimeSpan.FromSeconds(5),
        Retries = 1,
        ThrowDnsErrors = false
    });

    public async Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct = default)
    {
        var response = await QueryAsync(name, QueryType.TXT, ct).ConfigureAwait(false);
        // A TXT record may be split into several character-strings; they form one logical value.
        return response.Answers.TxtRecords().Select(record => string.Concat(record.Text)).ToList();
    }

    public async Task<IReadOnlyList<string>> MxAsync(string name, CancellationToken ct = default)
    {
        var response = await QueryAsync(name, QueryType.MX, ct).ConfigureAwait(false);
        return response.Answers.MxRecords()
            .OrderBy(record => record.Preference)
            .Select(record => record.Exchange.Value.TrimEnd('.').ToLowerInvariant())
            .ToList();
    }

    /// <summary>DnsClient's <c>TimeToLive</c> is documented as the record's current TTL; the host resolver
    /// already counts down the TTL of an answer it serves from its cache.</summary>
    public async Task<int?> MxTimeToLiveAsync(string name, CancellationToken ct = default)
    {
        var response = await QueryAsync(name, QueryType.MX, ct).ConfigureAwait(false);
        var records = response.Answers.MxRecords().ToList();
        return records.Count == 0 ? null : records.Min(record => record.TimeToLive);
    }

    public async Task<IReadOnlyList<string>> AddressesAsync(string host, CancellationToken ct = default)
    {
        var v4 = await QueryAsync(host, QueryType.A, ct).ConfigureAwait(false);
        var v6 = await QueryAsync(host, QueryType.AAAA, ct).ConfigureAwait(false);
        return v4.Answers.ARecords().Select(record => record.Address.ToString())
            .Concat(v6.Answers.AaaaRecords().Select(record => record.Address.ToString()))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> PtrAsync(string address, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(address, out var ip)) return [];
        IDnsQueryResponse response;
        try
        {
            response = await _client.QueryReverseAsync(ip, ct).ConfigureAwait(false);
        }
        catch (DnsResponseException ex)
        {
            throw new MailDnsLookupException($"Reverse lookup of {address} failed.", ex);
        }
        ThrowOnResolverFailure(response, address);
        return response.Answers.PtrRecords().Select(record => record.PtrDomainName.Value.TrimEnd('.').ToLowerInvariant()).ToList();
    }

    private async Task<IDnsQueryResponse> QueryAsync(string name, QueryType type, CancellationToken ct)
    {
        IDnsQueryResponse response;
        try
        {
            response = await _client.QueryAsync(name, type, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (DnsResponseException ex)
        {
            throw new MailDnsLookupException($"DNS {type} lookup of {name} failed.", ex);
        }
        ThrowOnResolverFailure(response, name);
        return response;
    }

    // NXDOMAIN and NOERROR/no-data are answers ("missing"); SERVFAIL, REFUSED and timeouts are not.
    private static void ThrowOnResolverFailure(IDnsQueryResponse response, string name)
    {
        if (response.HasError && response.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
            throw new MailDnsLookupException($"DNS lookup of {name} failed: {response.ErrorMessage}");
    }
}
