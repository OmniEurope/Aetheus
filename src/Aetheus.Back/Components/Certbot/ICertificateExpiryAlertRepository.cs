// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Certbot;

/// <summary>A certificate that is not expired yet and expires within the longest lead time.</summary>
internal sealed record ExpiringCertificate(int ServerId, string ServerName, string Name, string DomainsJson, DateTime ExpiryDate);

/// <summary>Recette R-69: the certificates the expiry alerts concern, the marks that make them raised once, and the lead times.</summary>
internal interface ICertificateExpiryAlertRepository
{
    /// <summary>The saved lead times row (comma separated), or null while none was ever saved.</summary>
    Task<string?> GetLeadDaysRowAsync(CancellationToken ct = default);

    Task SaveLeadDaysRowAsync(string value, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Certificates expiring after <paramref name="nowUtc"/> and no later than <paramref name="limitUtc"/>.</summary>
    Task<List<ExpiringCertificate>> GetCertificatesExpiringAsync(DateTime nowUtc, DateTime limitUtc, CancellationToken ct = default);

    /// <summary>The marks of the certificates that have not expired yet.</summary>
    Task<List<CertificateExpiryAlertMark>> GetActiveMarksAsync(DateTime nowUtc, CancellationToken ct = default);

    Task AddMarksAsync(IReadOnlyCollection<CertificateExpiryAlertMark> marks, CancellationToken ct = default);

    /// <summary>Forgets the marks of certificates that expired (or were renewed) before <paramref name="beforeUtc"/>.</summary>
    Task<int> RemoveMarksExpiredBeforeAsync(DateTime beforeUtc, CancellationToken ct = default);
}
