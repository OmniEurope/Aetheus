// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Certbot;

internal sealed class CertificateExpiryAlertRepository(AppDbContext db) : ICertificateExpiryAlertRepository
{
    public Task<string?> GetLeadDaysRowAsync(CancellationToken ct = default) =>
        db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key == CertificateAlertLeadTimeRules.LeadDaysKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);

    public async Task SaveLeadDaysRowAsync(string value, DateTime nowUtc, CancellationToken ct = default)
    {
        var setting = await db.AppSettings
            .FirstOrDefaultAsync(row => row.Key == CertificateAlertLeadTimeRules.LeadDaysKey, ct).ConfigureAwait(false);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = CertificateAlertLeadTimeRules.LeadDaysKey, Value = value, UpdatedAt = nowUtc });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = nowUtc;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<List<ExpiringCertificate>> GetCertificatesExpiringAsync(DateTime nowUtc, DateTime limitUtc, CancellationToken ct = default) =>
        db.CertbotCertificates.AsNoTracking()
            .Where(certificate => certificate.ExpiryDate > nowUtc && certificate.ExpiryDate <= limitUtc)
            .OrderBy(certificate => certificate.ExpiryDate)
            .Select(certificate => new ExpiringCertificate(
                certificate.ServerId, certificate.Server.Name, certificate.Name, certificate.Domains, certificate.ExpiryDate))
            .ToListAsync(ct);

    public Task<List<CertificateExpiryAlertMark>> GetActiveMarksAsync(DateTime nowUtc, CancellationToken ct = default) =>
        db.CertificateExpiryAlertMarks.AsNoTracking()
            .Where(mark => mark.ExpiryDate > nowUtc)
            .ToListAsync(ct);

    public async Task AddMarksAsync(IReadOnlyCollection<CertificateExpiryAlertMark> marks, CancellationToken ct = default)
    {
        db.CertificateExpiryAlertMarks.AddRange(marks);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> RemoveMarksExpiredBeforeAsync(DateTime beforeUtc, CancellationToken ct = default)
    {
        var old = await db.CertificateExpiryAlertMarks.Where(mark => mark.ExpiryDate < beforeUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        if (old.Count == 0) return 0;
        db.CertificateExpiryAlertMarks.RemoveRange(old);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return old.Count;
    }
}
