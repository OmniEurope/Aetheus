// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Settings;

/// <summary>The platform e-mail sender as the settings page reads and saves it.</summary>
public interface IEmailSenderSettingsService
{
    Task<EmailSenderSettingsDto> GetAsync(CancellationToken ct = default);
    Task<EmailSenderSettingsDto> UpdateAsync(UpdateEmailSenderSettingsRequest request, CancellationToken ct = default);
}
