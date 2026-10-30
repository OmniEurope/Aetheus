// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Certbot;

/// <summary>Recette R-69: the lead times of the certificate expiry alerts, set by an administrator in the platform settings.</summary>
[ApiController]
[Route("api/settings/certificate-alerts")]
[Authorize(Roles = "Admin")]
public class CertificateAlertSettingsController(ICertificateExpiryAlertService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CertificateAlertSettingsDto>> Get(CancellationToken ct) =>
        Ok(await service.GetSettingsAsync(ct));

    [HttpPut]
    public async Task<ActionResult<CertificateAlertSettingsDto>> Update(
        [FromBody] UpdateCertificateAlertSettingsRequest request, CancellationToken ct) =>
        Ok(await service.UpdateSettingsAsync(request, ct));
}
