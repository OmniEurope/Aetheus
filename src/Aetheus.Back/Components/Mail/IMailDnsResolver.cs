// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail;

/// <summary>Resolves TXT and MX alongside other mail DNS records. Empty results mean NXDOMAIN or no
/// data; <see cref="MailDnsLookupException"/> means verification could not complete.</summary>
public interface IMailDnsResolver
{
    Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<string>> MxAsync(string name, CancellationToken ct = default);

    /// <summary>The remaining time-to-live, in seconds, of the MX answer the resolver gives for
    /// <paramref name="name"/> (the shortest of its records), or null when there is no MX
    /// answer.</summary>
    Task<int?> MxTimeToLiveAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<string>> AddressesAsync(string host, CancellationToken ct = default);
    Task<IReadOnlyList<string>> PtrAsync(string address, CancellationToken ct = default);
}

public sealed class MailDnsLookupException(string message, Exception? inner = null) : Exception(message, inner);
