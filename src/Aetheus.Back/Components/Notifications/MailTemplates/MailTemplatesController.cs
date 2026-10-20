// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>The templates of the platform mails (recette R1-75), administrators only.</summary>
[ApiController]
[Route("api/mail-templates")]
[Authorize(Roles = "Admin")]
public sealed class MailTemplatesController(IMailTemplateService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<MailTemplateSummaryDto>>> List(CancellationToken ct) =>
        Ok(await service.ListAsync(ct));

    [HttpGet("{kind}/{language}")]
    public async Task<ActionResult<MailTemplateDto>> Get(MailTemplateKind kind, [StringLength(8)] string language, CancellationToken ct) =>
        Ok(await service.GetAsync(kind, language, ct));

    [HttpPut("{kind}/{language}")]
    public async Task<ActionResult<MailTemplateDto>> Save(
        MailTemplateKind kind, [StringLength(8)] string language, [FromBody] SaveMailTemplateRequest request, CancellationToken ct) =>
        Ok(await service.SaveAsync(kind, language, request, User.Identity?.Name, ct));

    /// <summary>Restores the default template; returns it.</summary>
    [HttpDelete("{kind}/{language}")]
    public async Task<ActionResult<MailTemplateDto>> Reset(MailTemplateKind kind, [StringLength(8)] string language, CancellationToken ct) =>
        Ok(await service.ResetAsync(kind, language, ct));

    /// <summary>Sends the draft, rendered with the samples, to the caller's own address.</summary>
    [HttpPost("{kind}/{language}/test")]
    public async Task<ActionResult<MailTemplateTestResultDto>> SendTest(
        MailTemplateKind kind, [StringLength(8)] string language, [FromBody] SaveMailTemplateRequest request, CancellationToken ct)
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userId = int.TryParse(claim, out var parsed) ? parsed : (int?)null;
        return Ok(await service.SendTestAsync(kind, language, request, userId, ct));
    }
}
