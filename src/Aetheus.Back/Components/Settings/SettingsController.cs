// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Components.Settings;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class SettingsController(ISettingsService settingsService, IEmailSenderSettingsService emailSender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<AppSettingDto>>> GetSettings(CancellationToken ct)
    {
        return Ok(await settingsService.GetSettingsAsync(ct));
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> UpdateSetting([StringLength(200)] string key, [FromBody] AppSettingDto setting, CancellationToken ct)
    {
        await settingsService.UpdateSettingAsync(key, setting.Value, ct);
        return Ok();
    }

    /// <summary>Recette R1-16: the platform e-mail sender, default no-reply account or custom SMTP server.</summary>
    [HttpGet("email-sender")]
    public async Task<ActionResult<EmailSenderSettingsDto>> GetEmailSender(CancellationToken ct) =>
        Ok(await emailSender.GetAsync(ct));

    [HttpPut("email-sender")]
    public async Task<ActionResult<EmailSenderSettingsDto>> UpdateEmailSender(
        [FromBody] UpdateEmailSenderSettingsRequest request, CancellationToken ct) =>
        Ok(await emailSender.UpdateAsync(request, ct));

    [HttpGet("secrets")]
    public async Task<ActionResult<List<SecretDto>>> GetSecrets(CancellationToken ct)
    {
        return Ok(await settingsService.GetSecretsAsync(ct));
    }

    [HttpPost("secrets")]
    public async Task<ActionResult<SecretDto>> CreateSecret([FromBody] CreateSecretRequest request, CancellationToken ct)
    {
        return Ok(await settingsService.CreateSecretAsync(request, ct));
    }

    [HttpDelete("secrets/{id:int}")]
    public async Task<IActionResult> DeleteSecret(int id, CancellationToken ct)
    {
        var deleted = await settingsService.DeleteSecretAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
