// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Services.Email;
using static Aetheus.Back.Components.Notifications.MailTemplates.UserWelcomeMailVariables;

namespace Aetheus.Back.Components.Auth;

/// <summary>Owner decision 2026-10-28: the mail telling a person their account newly holds the site Admin role.</summary>
public interface ISiteAdminGrantMail
{
    /// <summary>Sends the mail and audits <c>SiteAdminMailSent</c>, or <c>SiteAdminMailNotSent</c> with the reason.
    /// Never throws for a mail that did not leave: the grant it announces stands either way.</summary>
    Task SendAsync(int userId, string username, string? email, CancellationToken ct = default);
}

public sealed class SiteAdminGrantMail(
    IMailTemplateRenderer templates,
    IEmailSender sender,
    IConfiguration configuration,
    IAuditService audit,
    ILogger<SiteAdminGrantMail> logger) : ISiteAdminGrantMail
{
    public async Task SendAsync(int userId, string username, string? email, CancellationToken ct = default)
    {
        string? failure;
        try
        {
            failure = await TrySendAsync(username, email, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Mandatory: the role is already granted, and a mail that could not even be written must still leave
            // its trace in the audit, which an exception escaping to the domain event dispatcher would not.
            logger.LogError(exception, "[SiteAdmin] The site administrator mail of user {UserId} could not be written or sent", userId);
            failure = "the mail could not be written or sent (see the server log)";
        }

        if (failure is null)
        {
            await audit.LogAsync("SiteAdminMailSent", "User", userId, username, ct).ConfigureAwait(false);
            return;
        }
        logger.LogWarning("[SiteAdmin] Site administrator mail of user {UserId} not sent: {Reason}", userId, failure);
        await audit.LogAsync("SiteAdminMailNotSent", "User", userId, $"{username}: {failure}", ct).ConfigureAwait(false);
    }

    // Null when the mail left, else why it did not.
    private async Task<string?> TrySendAsync(string username, string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            return "the account has no e-mail address";
        var signInUrl = EmailHtmlLayout.Url(PasswordSetupService.PublicOrigin(configuration), "/login");
        if (signInUrl is null)
            return "no public address of the application is configured (Cors:FrontendUrl or Cors:Origins)";

        var style = await templates.ResolveRecipientAsync(email, ct).ConfigureAwait(false);
        var mail = await templates.RenderAsync(MailTemplateKind.SiteAdminGranted, MailTemplateData.Text(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [UserName] = username,
                [SignInEmail] = email,
                [SignInUrl] = signInUrl
            }), style, ct).ConfigureAwait(false);
        var sent = await sender.SendAsync(new EmailMessage(email, mail.Subject, mail.Text, mail.Html), ct).ConfigureAwait(false);
        return sent.Status == EmailSendStatus.Sent ? null : $"{sent.Status}: {sent.Message}";
    }
}
