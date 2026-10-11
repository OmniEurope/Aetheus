// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>
/// The sender e-mails leave from, or why none can be used. The no-reply fields describe the default
/// sender whatever the source, so the settings page can offer to prepare it.
/// </summary>
public sealed record EmailSenderResolution
{
    public EmailSenderSource Source { get; init; }
    public EmailSenderConfiguration? Configuration { get; init; }
    public EmailSenderProblem Problem { get; init; }
    public string? ProblemDetail { get; init; }

    public string? NoReplyAddress { get; init; }
    public int? NoReplyServerId { get; init; }

    /// <summary>The no-reply mailbox row, or null when the domain has none.</summary>
    public int? NoReplyAccountId { get; init; }

    /// <summary>Why nothing can be sent, in words for an API response or a delivery row.</summary>
    public string Describe() => Problem switch
    {
        EmailSenderProblem.None => "An e-mail sender is configured.",
        EmailSenderProblem.NoMailDomain =>
            "No e-mail sender: Aetheus manages no mail domain and no custom SMTP server is set in the platform settings.",
        EmailSenderProblem.NoReplyAccountMissing =>
            $"No e-mail sender: the mailbox {ProblemDetail} does not exist yet; prepare it from the platform settings (E-mail tab).",
        EmailSenderProblem.NoReplyPasswordUnknown =>
            $"No e-mail sender: Aetheus holds no password for {ProblemDetail}; prepare it from the platform settings (E-mail tab).",
        EmailSenderProblem.CustomIncomplete =>
            "No e-mail sender: the custom SMTP server lacks its host or its sender address.",
        EmailSenderProblem.CustomPasswordMissing =>
            "No e-mail sender: the custom SMTP server names a user name but no password is stored.",
        _ => "No e-mail sender."
    };
}
