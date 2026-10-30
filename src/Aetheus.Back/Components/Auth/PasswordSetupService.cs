// SPDX-License-Identifier: EUPL-1.2
using System.Buffers.Text;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Back.Services.Email;
using Microsoft.Extensions.Caching.Memory;
using static Aetheus.Back.Components.Notifications.MailTemplates.UserWelcomeMailVariables;

namespace Aetheus.Back.Components.Auth;

/// <summary>
/// Recette R-14 (owner decisions of 2026-10-28): the links with which a person chooses their password. The
/// welcome mail of a created account, a new link an administrator sends, and the "forgotten password" link
/// the person asks for. A mail never carries a password.
/// </summary>
public interface IPasswordSetupService
{
    /// <summary>Issues a token and sends the welcome mail. Never throws for a mail that did not leave: the
    /// result says what happened, and a mail that did not leave is logged.</summary>
    Task<WelcomeMailResultDto> SendWelcomeAsync(int userId, string username, string email, CancellationToken ct = default);

    /// <summary>An administrator sends a new link: the account's unused links are voided first. Null when the
    /// account was already sent <see cref="PasswordSetupService.MaxAdminLinksPerHour"/> links within the hour.
    /// Throws <see cref="NotFoundException"/> for no account, <see cref="ConflictException"/> for an inactive one.</summary>
    Task<WelcomeMailResultDto?> SendAdminLinkAsync(int userId, CancellationToken ct = default);

    /// <summary>"Forgotten password": queues the work and returns at once, so neither the answer nor its
    /// timing says whether an active account holds the address.</summary>
    void RequestReset(string email);

    /// <summary>The queued part of <see cref="RequestReset"/>: sends a one-hour link to the active account
    /// holding the address, within <see cref="PasswordSetupService.MaxResetsPerHour"/> an hour; nothing otherwise.</summary>
    Task ProcessResetAsync(string email, CancellationToken ct = default);

    /// <summary>Sets the password with a token; false (and nothing changed) for an unknown, spent or
    /// expired token, or an account no longer active.</summary>
    Task<bool> CompleteAsync(CompletePasswordSetupRequest request, CancellationToken ct = default);
}

public sealed class PasswordSetupService(
    IPasswordSetupRepository repo,
    IAuthRepository authRepo,
    IMailTemplateRenderer templates,
    IEmailSender sender,
    JwtOptions jwtOptions,
    TimeProvider timeProvider,
    IConfiguration configuration,
    IHttpContextAccessor httpContextAccessor,
    IMemoryCache cache,
    IAuditService audit,
    IDomainEventDispatcher domainEvents,
    IBackgroundTaskQueue backgroundQueue,
    ILogger<PasswordSetupService> logger) : IPasswordSetupService
{
    /// <summary>
    /// How long a welcome link (or one an administrator sends) stays usable. Longer than a forgotten-password
    /// link: a new colleague may read the mail days later. Single use either way.
    /// </summary>
    public static readonly TimeSpan WelcomeLifetime = TimeSpan.FromDays(7);

    /// <summary>How long a forgotten-password link stays usable (owner decision 2026-10-28).</summary>
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    /// <summary>Forgotten-password links one account can be sent per hour, whoever asks.</summary>
    public const int MaxResetsPerHour = 3;

    /// <summary>Links an administrator can send one account per hour.</summary>
    public const int MaxAdminLinksPerHour = 5;

    internal const string NoFrontAddressMessage =
        "No public address of the application is configured (Cors:FrontendUrl or Cors:Origins), so the mail could not link to the password page; nothing was sent.";

    // The same public address the notification mails link to.
    private string? FrontOrigin => PublicOrigin(configuration);

    /// <summary>The public address of the application the account mails link to.</summary>
    internal static string? PublicOrigin(IConfiguration configuration) =>
        configuration["Cors:Origins:0"] is { Length: > 0 } front ? front : configuration["Cors:FrontendUrl"];

    public async Task<WelcomeMailResultDto> SendWelcomeAsync(int userId, string username, string email, CancellationToken ct = default)
    {
        // The new account has no language yet: it is written in the language of the administrator creating it.
        var language = await CallerLanguageAsync(ct).ConfigureAwait(false);
        var result = await IssueAndSendAsync(userId, username, email, PasswordSetupPurpose.Welcome, language, ct).ConfigureAwait(false);
        if (result.Status == WelcomeMailStatus.Sent)
            await audit.LogAsync("WelcomeMailSent", "User", userId, username, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<WelcomeMailResultDto?> SendAdminLinkAsync(int userId, CancellationToken ct = default)
    {
        var user = await repo.FindUserAsync(userId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"User {userId} not found.");
        if (!user.IsActive || string.IsNullOrWhiteSpace(user.Email))
            throw new ConflictException("This account is inactive or has no e-mail address: no link can be sent to it.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (await repo.CountIssuedSinceAsync(userId, PasswordSetupPurpose.AdminLink, now - TimeSpan.FromHours(1), ct).ConfigureAwait(false) >= MaxAdminLinksPerHour)
        {
            await audit.LogAsync("PasswordLinkRefused.RateLimited", "User", userId, user.Username, ct).ConfigureAwait(false);
            return null;
        }

        // Only the newest link works: one sent earlier, maybe to a mailbox the person no longer reads, stops.
        await repo.VoidOpenAsync(userId, now, ct).ConfigureAwait(false);
        var language = await CallerLanguageAsync(ct).ConfigureAwait(false);
        var result = await IssueAndSendAsync(userId, user.Username, user.Email, PasswordSetupPurpose.AdminLink, language, ct).ConfigureAwait(false);
        await audit.LogAsync(result.Status == WelcomeMailStatus.Sent ? "PasswordLinkSent" : "PasswordLinkNotSent", "User", userId,
            $"{user.Username}: {result.Status}", ct).ConfigureAwait(false);
        return result;
    }

    public void RequestReset(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        backgroundQueue.Enqueue((services, ct) => services.GetRequiredService<IPasswordSetupService>().ProcessResetAsync(email, ct));
    }

    public async Task ProcessResetAsync(string email, CancellationToken ct = default)
    {
        var address = UserEmail.Normalize(email);
        var user = address is null ? null : await repo.FindActiveByEmailAsync(address, ct).ConfigureAwait(false);
        if (user is null)
        {
            // The address itself stays out of the audit: it may be anyone's, mistyped or probed.
            await audit.LogAsync("PasswordResetRequested.NoAccount", "Authentication", null, "no active account holds the address", ct).ConfigureAwait(false);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (await repo.CountIssuedSinceAsync(user.Id, PasswordSetupPurpose.Reset, now - TimeSpan.FromHours(1), ct).ConfigureAwait(false) >= MaxResetsPerHour)
        {
            await audit.LogAsync("PasswordResetRequested.RateLimited", "User", user.Id, user.Username, ct).ConfigureAwait(false);
            return;
        }

        // The person's own language; English for an account that never chose one.
        var result = await IssueAndSendAsync(user.Id, user.Username, user.Email!, PasswordSetupPurpose.Reset, MailTemplateLanguages.English, ct).ConfigureAwait(false);
        await audit.LogAsync(result.Status == WelcomeMailStatus.Sent ? "PasswordResetMailSent" : "PasswordResetMailNotSent", "User", user.Id,
            $"{user.Username}: {result.Status}", ct).ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(CompletePasswordSetupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (await repo.TryClaimAsync(Hash(request.Token), now, ct).ConfigureAwait(false) is not { } userId)
            return false;
        var user = await repo.FindUserTrackedAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return false;

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        // The person chose this password themselves: no forced change at their next sign-in.
        user.MustChangePassword = false;
        user.UpdatedAt = now;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await repo.VoidOpenAsync(userId, now, ct).ConfigureAwait(false);
        // Any session opened with the former password ends with it.
        await authRepo.RevokeAllUserRefreshTokensAsync(userId, ct).ConfigureAwait(false);
        cache.Remove($"sec-stamp:{userId}");
        await audit.LogAsync("PasswordSet", "User", userId, user.Username, ct).ConfigureAwait(false);
        await domainEvents.DispatchAsync(new UserSessionEndedEvent(userId, UserSessionEndReasons.PasswordChanged), ct).ConfigureAwait(false);
        // The link reached the account's mailbox: as good a proof as a sign-in for the administrator bootstrap.
        await domainEvents.DispatchAsync(new AccountAccessProvedEvent(userId, user.Username, user.Email), ct).ConfigureAwait(false);
        return true;
    }

    private async Task<WelcomeMailResultDto> IssueAndSendAsync(
        int userId, string username, string email, PasswordSetupPurpose purpose, string defaultLanguage, CancellationToken ct)
    {
        var signInUrl = EmailHtmlLayout.Url(FrontOrigin, "/login");
        if (signInUrl is null)
            return Logged(userId, new WelcomeMailResultDto { Status = WelcomeMailStatus.NotConfigured, Reason = WelcomeMailReason.NoPublicAddress, To = email, Message = NoFrontAddressMessage });

        var reset = purpose == PasswordSetupPurpose.Reset;
        var lifetime = reset ? ResetLifetime : WelcomeLifetime;
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await repo.AddAsync(new PasswordSetupToken
        {
            UserId = userId,
            TokenHash = Hash(token),
            Purpose = purpose,
            CreatedAt = now,
            ExpiresAt = now + lifetime
        }, ct).ConfigureAwait(false);

        var style = await templates.ResolveRecipientAsync(email, defaultLanguage, ct).ConfigureAwait(false);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [UserName] = username,
            [SignInEmail] = email,
            [SetPasswordUrl] = EmailHtmlLayout.Url(FrontOrigin, PasswordSetupLink.Path(token)),
            [SignInUrl] = signInUrl
        };
        if (reset) values[ValidMinutes] = ((int)lifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        else values[ValidDays] = ((int)lifetime.TotalDays).ToString(CultureInfo.InvariantCulture);
        var mail = await templates.RenderAsync(reset ? MailTemplateKind.PasswordReset : MailTemplateKind.UserWelcome,
            MailTemplateData.Text(values), style, ct).ConfigureAwait(false);
        var sent = await sender.SendAsync(new EmailMessage(email, mail.Subject, mail.Text, mail.Html), ct).ConfigureAwait(false);

        return Logged(userId, new WelcomeMailResultDto
        {
            Status = sent.Status switch
            {
                EmailSendStatus.Sent => WelcomeMailStatus.Sent,
                EmailSendStatus.NotConfigured => WelcomeMailStatus.NotConfigured,
                _ => WelcomeMailStatus.Failed
            },
            Reason = sent.Status == EmailSendStatus.NotConfigured ? WelcomeMailReason.NoSender : WelcomeMailReason.None,
            To = email,
            Message = sent.Message
        });
    }

    private string Hash(string token) => AuthTokenHelper.HashToken(jwtOptions.SigningKey, token);

    private async Task<string> CallerLanguageAsync(CancellationToken ct)
    {
        var caller = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name);
        var admin = string.IsNullOrEmpty(caller) ? null : await authRepo.FindUserWithRolesAsync(caller, ct).ConfigureAwait(false);
        return (await templates.ResolveRecipientAsync(admin?.Email, ct).ConfigureAwait(false)).Language;
    }

    private WelcomeMailResultDto Logged(int userId, WelcomeMailResultDto result)
    {
        if (result.Status != WelcomeMailStatus.Sent)
            logger.LogWarning("[PasswordSetup] Set-password mail of user {UserId} not sent ({Status}): {Reason}",
                userId, result.Status, result.Message);
        return result;
    }
}
