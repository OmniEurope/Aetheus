// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class MailState
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string PostfixVersion { get; set; } = string.Empty;
    public string DovecotVersion { get; set; } = string.Empty;
    public bool IsPostfixRunning { get; set; }
    public bool IsDovecotRunning { get; set; }
    public int QueueSize { get; set; }
    public DateTime LastUpdated { get; set; }

    // Inventory reported by the agent collector.
    public string Hostname { get; set; } = string.Empty;
    public bool IsOpenDkimRunning { get; set; }
    public bool IsManagedByAetheus { get; set; }
    public int HelperVersion { get; set; }

    /// <summary>DEP-005: when the stored mailbox quotas were last pushed to Dovecot after the helper reached the
    /// quota version; null until then, and reset to null while the helper is older (so each upgrade resyncs once).</summary>
    public DateTime? QuotaResyncedAt { get; set; }
    public string TlsCertPath { get; set; } = string.Empty;
    public bool TlsIsReadable { get; set; }
    public string TlsSubject { get; set; } = string.Empty;
    public string TlsIssuer { get; set; } = string.Empty;
    public DateTime? TlsExpiresAt { get; set; }
    public bool TlsIsSelfSigned { get; set; }
    public string SpamFilterName { get; set; } = string.Empty;
    public bool IsSpamFilterInstalled { get; set; }
    public bool IsSpamFilterRunning { get; set; }
    public string SpamFilterVersion { get; set; } = string.Empty;
    public double? SpamRejectScore { get; set; }
    public double? SpamAddHeaderScore { get; set; }
    public double? SpamGreylistScore { get; set; }

    /// <summary>JSON array of <c>code|parameter</c> collector diagnostics; null when there are none.</summary>
    public string? DiagnosticsJson { get; set; }

    /// <summary>JSON of the outbound delivery state (<c>MailDeliveryStateDto</c>: retry timings, deferred queue
    /// entries, host IPv6 addresses); null when the agent reported nothing.</summary>
    public string? DeliveryJson { get; set; }

    // Navigation
    public Server Server { get; set; } = null!;
}
