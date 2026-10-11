// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail;

/// <summary>PLAN-005: the DNS lookups the mail verification needs. <see cref="System.Net.Dns"/> cannot read
/// TXT or MX records, hence a dedicated resolver (DnsClient, Apache-2.0). Every method returns an empty list
/// for NXDOMAIN / no data and throws <see cref="MailDnsLookupException"/> when the resolver itself failed,
/// so "no record" and "could not check" are never confused.</summary>
public interface IMailDnsResolver
{
    Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<string>> MxAsync(string name, CancellationToken ct = default);

    /// <summary>Recette R1-14: the remaining time-to-live, in seconds, of the MX answer the resolver gives for
    /// <paramref name="name"/> (the shortest of its records), or null when there is no MX answer.</summary>
    Task<int?> MxTimeToLiveAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<string>> AddressesAsync(string host, CancellationToken ct = default);
    Task<IReadOnlyList<string>> PtrAsync(string address, CancellationToken ct = default);
}

public sealed class MailDnsLookupException(string message, Exception? inner = null) : Exception(message, inner);
