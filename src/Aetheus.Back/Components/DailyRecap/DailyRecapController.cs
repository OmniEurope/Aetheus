// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>The daily recap e-mail, set up by an administrator in the platform settings.</summary>
[ApiController]
[Route("api/settings/daily-recap")]
[Authorize(Roles = "Admin")]
public class DailyRecapController(IDailyRecapService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DailyRecapSettingsDto>> Get(CancellationToken ct) =>
        Ok(await service.GetSettingsAsync(ct));

    [HttpPut]
    public async Task<ActionResult<DailyRecapSettingsDto>> Update([FromBody] UpdateDailyRecapSettingsRequest request, CancellationToken ct) =>
        Ok(await service.UpdateSettingsAsync(request, ct));

    [HttpGet("preview")]
    public async Task<ActionResult<DailyRecapPreviewDto>> Preview(CancellationToken ct) =>
        Ok(await service.PreviewAsync(ct));

    /// <summary>Sends yesterday's recap now to the saved recipients; the SMTP answer comes back verbatim.</summary>
    [HttpPost("send-now")]
    public async Task<ActionResult<DailyRecapSendResultDto>> SendNow(CancellationToken ct) =>
        Ok(await service.SendNowAsync(ct));
}
