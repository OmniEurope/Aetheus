// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// Records that the alert of one lead time was raised for one certificate expiring on one date
/// (R-69), so a check never raises it twice. It is keyed by the certificate's name and expiry date,
/// not by its row: the heartbeat replaces the certificate rows on every change, and a renewed
/// certificate (a new expiry date) must alert again.
/// </summary>
public class CertificateExpiryAlertMark
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string CertificateName { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }

    /// <summary>The lead time, in days before the expiry, this mark stands for.</summary>
    public int LeadDays { get; set; }

    public DateTime RaisedAt { get; set; }

    public Server Server { get; set; } = null!;
}
