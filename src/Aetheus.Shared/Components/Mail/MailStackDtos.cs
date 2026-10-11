// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Mail;

// PLAN-005: mail stack inventory, TLS, spam filter, DNS verification, delivery proof and diagnostics.

/// <summary>DKIM key referenced by the OpenDKIM KeyTable of the server.</summary>
public sealed record MailDkimKeyDto
{
    [StringLength(253)]
    public string Domain { get; init; } = string.Empty;
    [StringLength(63)]
    public string Selector { get; init; } = string.Empty;
    /// <summary>TXT value of the public key (<c>v=DKIM1; k=rsa; p=...</c>), empty when unreadable.</summary>
    [StringLength(4096)]
    public string PublicKey { get; init; } = string.Empty;
}

/// <summary>Certificate Postfix presents (<c>smtpd_tls_cert_file</c>), as read by the agent.</summary>
public sealed record MailTlsStateDto
{
    [StringLength(512)]
    public string CertPath { get; init; } = string.Empty;
    /// <summary>False when the agent could not read the certificate file (path known, content unknown).</summary>
    public bool IsReadable { get; init; }
    [StringLength(512)]
    public string Subject { get; init; } = string.Empty;
    [StringLength(512)]
    public string Issuer { get; init; } = string.Empty;
    public DateTime? ExpiresAt { get; init; }
    public bool IsSelfSigned { get; init; }
}

/// <summary>Spam filter detected on the server (rspamd only; other filters are reported by name).</summary>
public sealed record MailSpamFilterStateDto
{
    [StringLength(40)]
    public string Name { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    [StringLength(100)]
    public string Version { get; init; } = string.Empty;
    public double? RejectScore { get; init; }
    public double? AddHeaderScore { get; init; }
    public double? GreylistScore { get; init; }
}

/// <summary>Spam filter state and thresholds as exposed by the API.</summary>
public sealed record SpamFilterConfigDto
{
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public double? RejectScore { get; init; }
    public double? AddHeaderScore { get; init; }
    public double? GreylistScore { get; init; }
}

/// <summary>rspamd action thresholds. The service enforces greylist &lt; add_header &lt; reject.</summary>
public sealed record UpdateSpamFilterRequest
{
    [Range(0.0, 999.99)]
    public double RejectScore { get; init; } = 15;

    [Range(0.0, 999.99)]
    public double AddHeaderScore { get; init; } = 6;

    [Range(0.0, 999.99)]
    public double GreylistScore { get; init; } = 4;
}

/// <summary>Trains the rspamd classifier with one raw message.</summary>
public sealed record LearnSpamRequest
{
    public bool IsSpam { get; init; } = true;

    [Required]
    [StringLength(1_048_576, MinimumLength = 1)]
    public string RawMessage { get; init; } = string.Empty;
}

/// <summary>Mail TLS certificate as exposed by the API.</summary>
public sealed record MailCertificateDto
{
    public string Hostname { get; init; } = string.Empty;
    public string CertPath { get; init; } = string.Empty;
    public bool IsReadable { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public DateTime? ExpiresAt { get; init; }
    public bool IsSelfSigned { get; init; }
    /// <summary>True when Postfix already uses the Let's Encrypt lineage of the hostname.</summary>
    public bool UsesLetsEncryptLineage { get; init; }
}

/// <summary>Requests a Let's Encrypt certificate for the mail hostname, then installs it for Postfix and Dovecot.</summary>
public sealed record RequestMailCertificateRequest
{
    /// <summary>
    /// The Let's Encrypt account contact, which only receives expiry notices. Optional (recette R1-6): null
    /// registers the account without an address, as the certbot helper already allows.
    /// </summary>
    [EmailAddress]
    [StringLength(254)]
    public string? Email { get; init; }
}

/// <summary>Sends one tagged test message through the server's MTA.</summary>
public sealed record MailTestDeliveryRequest
{
    [Required]
    [EmailAddress]
    [StringLength(254, MinimumLength = 5)]
    public string From { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(254, MinimumLength = 5)]
    public string To { get; init; } = string.Empty;
}

/// <summary>One DNS record check of a mail domain.</summary>
public sealed record MailDnsRecordCheckDto
{
    public MailDnsRecordKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Expected { get; init; } = string.Empty;
    public List<string> Observed { get; init; } = [];
    public MailCheckVerdict Verdict { get; init; }
    public string Detail { get; init; } = string.Empty;

    /// <summary>Recette R1-14: for an MX that still shows another host, the remaining time-to-live (seconds) of
    /// the answer seen, as the resolver reported it; null when it was not reported.</summary>
    public int? TimeToLiveSeconds { get; init; }
}

/// <summary>Result of the DNS verification of one mail domain.</summary>
public sealed record MailDnsCheckDto
{
    public int DomainId { get; init; }
    public string Domain { get; init; } = string.Empty;
    public DateTime CheckedAt { get; init; }
    public List<MailDnsRecordCheckDto> Records { get; init; } = [];
}

/// <summary>Current public MX hosts of a domain before its mail stack is configured.</summary>
public sealed record MailMxPreviewDto
{
    [StringLength(253)]
    public string Domain { get; init; } = string.Empty;
    [MaxLength(20)]
    public List<string> Hosts { get; init; } = [];
    public MailCheckVerdict Verdict { get; init; }
}

/// <summary>Parsed <c>DELIVERY</c> line of a <c>MailSendTest</c> task.</summary>
public sealed record MailDeliveryResultDto
{
    public string Status { get; init; } = string.Empty;
    public string QueueId { get; init; } = string.Empty;
    public string MessageMarker { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool IsSent => Status == "sent";
}

/// <summary>Parsed <c>CHECK</c> line of a <c>MailTestConfig</c> task.</summary>
public sealed record MailComponentCheckDto
{
    public string Component { get; init; } = string.Empty;
    public bool IsOk { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>One line of the mail diagnostics checklist computed by the backend.</summary>
public sealed record MailDiagnosticItemDto
{
    public string Key { get; init; } = string.Empty;
    public MailCheckVerdict Verdict { get; init; }
    public string Detail { get; init; } = string.Empty;

    /// <summary>Recette R2-090: for a DNS line (A, PTR, MX, SPF, DKIM, DMARC), the record the line expects,
    /// so the page shows exactly what to publish. Empty for the other lines.</summary>
    public string RecordName { get; init; } = string.Empty;

    /// <summary>DNS type of <see cref="RecordValue"/>: <c>A</c>, <c>AAAA</c>, <c>PTR</c>, <c>MX</c> or <c>TXT</c>.</summary>
    public string RecordType { get; init; } = string.Empty;

    /// <summary>Expected value; empty when it is not known yet (a DKIM key the agent has not read).</summary>
    public string RecordValue { get; init; } = string.Empty;

    /// <summary>Recette R1-14: on a red MX line, the remaining time-to-live (seconds) of the answer seen, as the
    /// resolver reported it; null when it was not reported.</summary>
    public int? TimeToLiveSeconds { get; init; }
}

/// <summary>Mail diagnostics computed from the last heartbeat and backend DNS lookups.</summary>
public sealed record MailDiagnosticsDto
{
    public DateTime CheckedAt { get; init; }
    public List<MailDiagnosticItemDto> Items { get; init; } = [];
}

/// <summary>Id of the server task a mail endpoint queued, so the client can match its completion.</summary>
public sealed record MailTaskQueuedDto
{
    public int TaskId { get; init; }
}

/// <summary>
/// "Réinstaller la messagerie" (recette R2-083 / R2-088): erases the whole mail stack of the server. The
/// caller types back the mail domain (or the mail host name when no domain is known) as proof of intent;
/// the backend refuses any other word.
/// </summary>
public sealed record MailPurgeRequest
{
    [Required]
    [StringLength(253, MinimumLength = 1)]
    public string Confirmation { get; init; } = string.Empty;
}
