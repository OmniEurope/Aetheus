// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Settings;

/// <summary>A block of the daily recap e-mail. Each one reads data the platform already records.</summary>
public enum DailyRecapSection
{
    /// <summary>Parent pipeline runs that finished the day before; child runs are left out.</summary>
    Pipelines = 0,

    /// <summary>Each server: state, pending OS updates, agent version, free disk space.</summary>
    Servers = 1,

    /// <summary>Releases promoted or rolled back the day before.</summary>
    Releases = 2,

    /// <summary>Application backups that finished the day before.</summary>
    Backups = 3,

    /// <summary>Certbot certificates expiring soon.</summary>
    Certificates = 4,

    /// <summary>Vault secrets expiring soon.</summary>
    Secrets = 5,

    /// <summary>Security and quality findings first seen or resolved the day before.</summary>
    Findings = 6,

    /// <summary>The mail servers: Postfix, Dovecot and the queue.</summary>
    Mail = 7
}

/// <summary>The language the recap e-mail is written in.</summary>
public enum DailyRecapLanguage
{
    French = 0,
    English = 1
}

/// <summary>What became of a recap sent on request.</summary>
public enum DailyRecapSendStatus
{
    Sent = 0,
    Failed = 1,
    NotConfigured = 2
}

/// <summary>The daily recap e-mail settings, with what the scheduler did last.</summary>
public sealed record DailyRecapSettingsDto
{
    public bool Enabled { get; init; } = true;
    public TimeOnly SendTime { get; init; } = new(7, 0);
    public string TimeZoneId { get; init; } = "Europe/Paris";
    public DailyRecapLanguage Language { get; init; } = DailyRecapLanguage.French;
    public List<DailyRecapSection> Sections { get; init; } = [];

    /// <summary>The addresses the next recap goes to: the saved list, or the active administrators'
    /// e-mails until a list is saved (then <see cref="RecipientsAreDefault"/> is true).</summary>
    public List<string> Recipients { get; init; } = [];

    public bool RecipientsAreDefault { get; init; }

    /// <summary>The last day the scheduler sent the recap for (a local date of <see cref="TimeZoneId"/>).</summary>
    public DateOnly? LastSentDate { get; init; }

    /// <summary>When that scheduled send ran; null when it never ran.</summary>
    public DateTime? LastSentAt { get; init; }

    /// <summary>How many recipients that send addressed, and how many the SMTP server accepted.</summary>
    public int? LastRecipientCount { get; init; }
    public int? LastDeliveredCount { get; init; }

    /// <summary>The first refusal of that send, verbatim, or why nothing was sent; null when all went through.</summary>
    public string? LastError { get; init; }
}

public sealed record UpdateDailyRecapSettingsRequest
{
    public bool Enabled { get; init; } = true;

    public TimeOnly SendTime { get; init; } = new(7, 0);

    [Required]
    [StringLength(64)]
    public string TimeZoneId { get; init; } = "Europe/Paris";

    public DailyRecapLanguage Language { get; init; } = DailyRecapLanguage.French;

    [MaxLength(16)]
    public List<DailyRecapSection> Sections { get; init; } = [];

    [MaxLength(50)]
    [MaxItemStringLength(254)]
    public List<string> Recipients { get; init; } = [];
}

/// <summary>The recap as it would be sent now: subject, HTML body and plain-text alternative.</summary>
public sealed record DailyRecapPreviewDto
{
    public DateOnly Day { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string Html { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
}

public sealed record DailyRecapSendResultDto
{
    public DailyRecapSendStatus Status { get; init; }

    /// <summary>The SMTP verdict verbatim on a failure, or why nothing was attempted.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The addresses the send went to, and how many of them the SMTP server accepted.</summary>
    public List<string> Recipients { get; init; } = [];

    public int Delivered { get; init; }
}
