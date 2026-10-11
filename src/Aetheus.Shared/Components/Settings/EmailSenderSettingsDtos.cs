// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Settings;

/// <summary>
/// Recette R1-16: the platform e-mail sender. By default the no-reply account of the mail domain
/// Aetheus manages; a custom SMTP server replaces it when <see cref="UseCustom"/> is set. The password
/// never travels back: <see cref="HasPassword"/> only says whether one is stored.
/// </summary>
public sealed record EmailSenderSettingsDto
{
    public bool UseCustom { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 587;
    public SmtpTlsMode TlsMode { get; init; } = SmtpTlsMode.StartTls;
    public string Username { get; init; } = string.Empty;
    public bool HasPassword { get; init; }
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>The sender really used now, after the default and the custom server are weighed.</summary>
    public EmailSenderSource EffectiveSource { get; init; }
    public string? EffectiveFromAddress { get; init; }
    public string? EffectiveHost { get; init; }
    public int? EffectivePort { get; init; }
    public EmailSenderProblem Problem { get; init; }

    /// <summary>What the problem is about (a domain, an address), or null.</summary>
    public string? ProblemDetail { get; init; }

    /// <summary>The no-reply address of the default sender, when a managed mail domain exists.</summary>
    public string? NoReplyAddress { get; init; }

    /// <summary>True when Aetheus can create the no-reply mailbox, or replace its password, by itself.</summary>
    public bool CanPrepareNoReply { get; init; }
}

public sealed record UpdateEmailSenderSettingsRequest
{
    public bool UseCustom { get; init; }

    [StringLength(253)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    public SmtpTlsMode TlsMode { get; init; } = SmtpTlsMode.StartTls;

    [StringLength(254)]
    public string Username { get; init; } = string.Empty;

    /// <summary>A new password; null or empty keeps the stored one.</summary>
    [StringLength(500)]
    public string? Password { get; init; }

    [StringLength(254)]
    public string FromAddress { get; init; } = string.Empty;
}
