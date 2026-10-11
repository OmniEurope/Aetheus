// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Services.Email;

/// <summary>
/// Recette R1-16. The default sender needs no configuration: the <c>no-reply</c> mailbox of the mail
/// domain Aetheus manages, submitted on port 587 with STARTTLS and authenticated with the password
/// Aetheus generated and keeps in its secret store. A custom SMTP server selected in the platform
/// settings replaces it. Nothing here guesses a password: a missing one is reported as the problem.
/// </summary>
public sealed class EmailSenderConfigurationResolver(
    IEmailSenderSettingsRepository repo,
    IEncryptionService encryption) : IEmailSenderConfigurationResolver
{
    public async Task<EmailSenderResolution> ResolveAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetSettingsAsync(ct).ConfigureAwait(false);
        var noReply = await ResolveNoReplyAsync(ct).ConfigureAwait(false);
        var useCustom = string.Equals(
            Read(settings, EmailSenderSettingKeys.Mode), EmailSenderSettingKeys.CustomMode, StringComparison.OrdinalIgnoreCase);
        return useCustom ? await ResolveCustomAsync(settings, noReply, ct).ConfigureAwait(false) : noReply;
    }

    private async Task<EmailSenderResolution> ResolveCustomAsync(
        IReadOnlyDictionary<string, string> settings, EmailSenderResolution noReply, CancellationToken ct)
    {
        var host = Read(settings, EmailSenderSettingKeys.Host);
        var from = Read(settings, EmailSenderSettingKeys.FromAddress);
        var username = Read(settings, EmailSenderSettingKeys.Username);
        // The no-reply fields stay: the settings page still describes the default it would fall back to.
        var custom = noReply with { Source = EmailSenderSource.None, Configuration = null, ProblemDetail = null };
        if (host.Length == 0 || from.Length == 0)
            return custom with { Problem = EmailSenderProblem.CustomIncomplete };

        var password = string.Empty;
        if (username.Length > 0)
        {
            password = await ReadSecretAsync(EmailSenderSettingKeys.SmtpPasswordSecret, ct).ConfigureAwait(false) ?? string.Empty;
            if (password.Length == 0)
                return custom with { Problem = EmailSenderProblem.CustomPasswordMissing };
        }

        var port = int.TryParse(Read(settings, EmailSenderSettingKeys.Port), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 65535 ? parsed : EmailSenderSettingKeys.ManagedSubmissionPort;
        var tlsMode = Enum.TryParse<SmtpTlsMode>(Read(settings, EmailSenderSettingKeys.TlsMode), ignoreCase: true, out var mode)
            && Enum.IsDefined(mode) ? mode : SmtpTlsMode.StartTls;
        return custom with
        {
            Source = EmailSenderSource.Custom,
            Problem = EmailSenderProblem.None,
            Configuration = new EmailSenderConfiguration(host, port, tlsMode, username, password, from)
        };
    }

    private async Task<EmailSenderResolution> ResolveNoReplyAsync(CancellationToken ct)
    {
        if (await repo.FindDefaultMailDomainAsync(ct).ConfigureAwait(false) is not { } domain)
            return new EmailSenderResolution { Problem = EmailSenderProblem.NoMailDomain };

        var address = $"{EmailSenderSettingKeys.NoReplyLocalPart}@{domain.DomainName}";
        var resolution = new EmailSenderResolution { NoReplyAddress = address, NoReplyServerId = domain.ServerId };
        var accountId = await repo.FindActiveMailboxIdAsync(address, ct).ConfigureAwait(false);
        if (accountId is null)
            return resolution with { Problem = EmailSenderProblem.NoReplyAccountMissing, ProblemDetail = address };

        resolution = resolution with { NoReplyAccountId = accountId };
        var password = await ReadSecretAsync(EmailSenderSettingKeys.NoReplyPasswordSecret(address), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
            return resolution with { Problem = EmailSenderProblem.NoReplyPasswordUnknown, ProblemDetail = address };

        return resolution with
        {
            Source = EmailSenderSource.ManagedNoReply,
            Configuration = new EmailSenderConfiguration(
                domain.SubmissionHost, EmailSenderSettingKeys.ManagedSubmissionPort, SmtpTlsMode.StartTls,
                address, password, address)
        };
    }

    private async Task<string?> ReadSecretAsync(string key, CancellationToken ct)
    {
        var encrypted = await repo.GetEncryptedSecretAsync(key, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(encrypted) ? null : encryption.DecryptValue(encrypted);
    }

    private static string Read(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
}
