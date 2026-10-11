// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Mail;

/// <summary>Recette R1-16: prepares the no-reply mailbox the platform sends its e-mails from.</summary>
public interface IMailNoReplySenderService
{
    /// <summary>
    /// Creates the no-reply mailbox of the default mail domain, or gives the existing one a new
    /// password, with a password generated here and kept in the platform secret store.
    /// </summary>
    Task<EmailSenderSettingsDto> PrepareAsync(CancellationToken ct = default);
}
