// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Certbot;

/// <summary>Recette R-69: the lead times of the certificate expiry alerts, and the check that raises them.</summary>
public interface ICertificateExpiryAlertService
{
    Task<CertificateAlertSettingsDto> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Saves the list after refusing a value out of range or listed twice (<see cref="BadRequestException"/>).</summary>
    Task<CertificateAlertSettingsDto> UpdateSettingsAsync(UpdateCertificateAlertSettingsRequest request, CancellationToken ct = default);

    /// <summary>The number of days the Certbot page marks a certificate "expires soon" from.</summary>
    Task<CertificateExpiryWarningDto> GetExpiryWarningAsync(CancellationToken ct = default);
}
