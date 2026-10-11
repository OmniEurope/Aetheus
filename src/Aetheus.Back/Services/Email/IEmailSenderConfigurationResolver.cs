// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>Decides which sender e-mails leave from: the custom SMTP server when it is selected, otherwise the default no-reply account.</summary>
public interface IEmailSenderConfigurationResolver
{
    Task<EmailSenderResolution> ResolveAsync(CancellationToken ct = default);
}
