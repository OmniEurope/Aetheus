// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The templates of the platform mails (recette R1-75). Without an organization, the platform's templates: a
/// site Admin's. With <c>organizationId</c>, that organization's own versions (Administration section, owner
/// decision 2026-10-28): a site Admin, or a holder of org.mailtemplates.manage on that organization; anyone
/// else, the organization's administrators of other organizations included, is answered 403.
/// </summary>
[ApiController]
[Route("api/mail-templates")]
[Authorize]
public sealed class MailTemplatesController(IMailTemplateService service, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<MailTemplateSummaryDto>>> List(
        [FromQuery, Range(1, int.MaxValue)] int? organizationId, CancellationToken ct)
    {
        if (!await MayEditAsync(organizationId, ct)) return Forbid();
        return Ok(organizationId is { } organization
            ? await service.ListForOrganizationAsync(organization, ct)
            : await service.ListAsync(ct));
    }

    [HttpGet("{kind}/{language}")]
    public async Task<ActionResult<MailTemplateDto>> Get(
        MailTemplateKind kind, [StringLength(8)] string language, [FromQuery, Range(1, int.MaxValue)] int? organizationId, CancellationToken ct)
    {
        if (!await MayEditAsync(organizationId, ct)) return Forbid();
        return Ok(organizationId is { } organization
            ? await service.GetForOrganizationAsync(organization, kind, language, ct)
            : await service.GetAsync(kind, language, ct));
    }

    [HttpPut("{kind}/{language}")]
    public async Task<ActionResult<MailTemplateDto>> Save(
        MailTemplateKind kind, [StringLength(8)] string language, [FromBody] SaveMailTemplateRequest request,
        [FromQuery, Range(1, int.MaxValue)] int? organizationId, CancellationToken ct)
    {
        if (!await MayEditAsync(organizationId, ct)) return Forbid();
        return Ok(organizationId is { } organization
            ? await service.SaveForOrganizationAsync(organization, kind, language, request, User.Identity?.Name, ct)
            : await service.SaveAsync(kind, language, request, User.Identity?.Name, ct));
    }

    /// <summary>Restores the default template (for an organization, the platform's); returns it.</summary>
    [HttpDelete("{kind}/{language}")]
    public async Task<ActionResult<MailTemplateDto>> Reset(
        MailTemplateKind kind, [StringLength(8)] string language, [FromQuery, Range(1, int.MaxValue)] int? organizationId, CancellationToken ct)
    {
        if (!await MayEditAsync(organizationId, ct)) return Forbid();
        return Ok(organizationId is { } organization
            ? await service.ResetForOrganizationAsync(organization, kind, language, ct)
            : await service.ResetAsync(kind, language, ct));
    }

    /// <summary>Sends the draft, rendered with the samples, to the caller's own address.</summary>
    [HttpPost("{kind}/{language}/test")]
    public async Task<ActionResult<MailTemplateTestResultDto>> SendTest(
        MailTemplateKind kind, [StringLength(8)] string language, [FromBody] SaveMailTemplateRequest request,
        [FromQuery, Range(1, int.MaxValue)] int? organizationId, CancellationToken ct)
    {
        if (!await MayEditAsync(organizationId, ct)) return Forbid();
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userId = int.TryParse(claim, out var parsed) ? parsed : (int?)null;
        return Ok(await service.SendTestAsync(kind, language, request, userId, ct));
    }

    /// <summary>The platform's templates are a site Admin's; an organization's, a holder of
    /// org.mailtemplates.manage on it (the site Admin holds every key).</summary>
    private async Task<bool> MayEditAsync(int? organizationId, CancellationToken ct) =>
        organizationId is { } organization
            ? await authz.HasPermissionAsync(User, PermissionKeys.OrganizationMailTemplatesManage, organization, ct)
            : User.IsInRole(RoleNames.Admin);
}
