// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Notifications;

namespace Aetheus.Back.Components.Certbot;

/// <summary>
/// Recette R-69: certificate expiry alerts with lead times the administrator chooses. A check raises
/// the alert of a lead time once per certificate and expiry date: the mark of each lead time reached
/// is written first, then the event is sent, so a restart, a second instance or a failing channel never
/// makes it leave twice. A renewed certificate has a new expiry date and alerts again. When several lead
/// times are reached at once (a certificate first seen late, or a lead time added afterwards) one alert
/// leaves, for the nearest of them, and all of them are marked.
/// </summary>
internal sealed class CertificateExpiryAlertService(
    ICertificateExpiryAlertRepository repo,
    INotificationService notifications,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<CertificateExpiryAlertService> logger) : ICertificateExpiryAlertService
{
    /// <summary>The event the alert is sent as, so the notification rules of the user choose the channel.</summary>
    internal const string EventType = NotificationEventTypes.CertificateExpiring;

    /// <summary>How long the mark of an expired certificate is kept, after which it is forgotten.</summary>
    internal const int MarkRetentionDays = 30;

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<CertificateAlertSettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
        new() { LeadDays = await LeadDaysAsync(ct).ConfigureAwait(false) };

    public async Task<CertificateAlertSettingsDto> UpdateSettingsAsync(UpdateCertificateAlertSettingsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var leadDays = CertificateAlertLeadTimeRules.Validate(request.LeadDays);
        await repo.SaveLeadDaysRowAsync(CertificateAlertLeadTimeRules.Serialize(leadDays), Now, ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "Setting", null, "CertificateAlerts", ct).ConfigureAwait(false);
        return new CertificateAlertSettingsDto { LeadDays = leadDays };
    }

    public async Task<CertificateExpiryWarningDto> GetExpiryWarningAsync(CancellationToken ct = default) =>
        new() { Days = CertificateAlertLeadTimeRules.WarningDays(await LeadDaysAsync(ct).ConfigureAwait(false)) };

    /// <summary>One pass of the check. Returns how many alerts it raised, for the tests and the log.</summary>
    internal async Task<int> RunAsync(CancellationToken ct)
    {
        var now = Now;
        await repo.RemoveMarksExpiredBeforeAsync(now.AddDays(-MarkRetentionDays), ct).ConfigureAwait(false);
        var leadDays = await LeadDaysAsync(ct).ConfigureAwait(false);
        if (leadDays.Count == 0) return 0;

        var candidates = await repo.GetCertificatesExpiringAsync(now, now.AddDays(leadDays.Max()), ct).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;
        var raisedMarks = (await repo.GetActiveMarksAsync(now, ct).ConfigureAwait(false))
            .Select(mark => (mark.ServerId, mark.CertificateName, mark.ExpiryDate, mark.LeadDays))
            .ToHashSet();

        var raised = 0;
        foreach (var certificate in candidates)
        {
            var fresh = CertificateAlertLeadTimeRules.Crossed(leadDays, certificate.ExpiryDate, now)
                .Where(days => !raisedMarks.Contains((certificate.ServerId, certificate.Name, certificate.ExpiryDate, days)))
                .ToList();
            if (fresh.Count == 0) continue;

            await repo.AddMarksAsync([.. fresh.Select(days => new CertificateExpiryAlertMark
            {
                ServerId = certificate.ServerId,
                CertificateName = certificate.Name,
                ExpiryDate = certificate.ExpiryDate,
                LeadDays = days,
                RaisedAt = now
            })], ct).ConfigureAwait(false);
            await notifications.SendEventAsync(EventType, Payload(certificate, fresh.Min(), now), ct).ConfigureAwait(false);
            raised++;
            logger.LogInformation(
                "[CertificateExpiry] alert for certificate {Certificate} of server {Server}, lead time {LeadDays} day(s), expires {Expiry:u}",
                certificate.Name, certificate.ServerName, fresh.Min(), certificate.ExpiryDate);
        }
        return raised;
    }

    private async Task<List<int>> LeadDaysAsync(CancellationToken ct) =>
        CertificateAlertLeadTimeRules.Parse(await repo.GetLeadDaysRowAsync(ct).ConfigureAwait(false));

    private static object Payload(ExpiringCertificate certificate, int leadDays, DateTime now) => new
    {
        certificate.ServerId,
        certificate.ServerName,
        CertificateName = certificate.Name,
        Domains = Domains(certificate.DomainsJson),
        // The mail labels this field "Expires at (UTC)": the same name the secret expiry event uses.
        ExpiresAt = certificate.ExpiryDate,
        DaysRemaining = (certificate.ExpiryDate - now).Days,
        LeadDays = leadDays
    };

    private static string Domains(string json)
    {
        try
        {
            return string.Join(", ", JsonSerializer.Deserialize<List<string>>(json) ?? []);
        }
        catch (JsonException)
        {
            // Mandatory: the column is written by the agent heartbeat; a malformed value must not stop the alert.
            return string.Empty;
        }
    }
}
