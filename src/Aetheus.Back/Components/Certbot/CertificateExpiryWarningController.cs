// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Certbot;

/// <summary>
/// Recette R-69: the horizon the Certbot page marks a certificate "expires soon" from (the longest alert
/// lead time). A number of days and nothing else, so any signed-in user can read it, not only the administrators
/// who can read the settings.
/// </summary>
[ApiController]
[Route("api/certbot/expiry-warning")]
[Authorize]
public class CertificateExpiryWarningController(ICertificateExpiryAlertService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CertificateExpiryWarningDto>> Get(CancellationToken ct) =>
        Ok(await service.GetExpiryWarningAsync(ct));
}
