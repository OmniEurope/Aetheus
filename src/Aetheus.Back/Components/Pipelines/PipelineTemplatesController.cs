// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

[ApiController]
[Route("api/pipelines/templates")]
[Authorize]
public class PipelineTemplatesController(
    IPipelineTemplateService templateService,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<PipelineTemplateSummaryDto>>> GetTemplates(CancellationToken ct)
    {
        var templates = await templateService.GetTemplatesAsync(ct);
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.PipelineTemplate, Permission.Read, ct);
        var writableIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.PipelineTemplate, Permission.Write, ct);
        if (accessibleIds is null) return Ok(WithCanEdit(templates, writableIds));
        var own = templates.Where(template => accessibleIds.Contains(template.Id)).ToList();
        if (!await PipelineTemplateReadRule.MayReadSharedCatalogAsync(authz, User, ct))
            return Ok(WithCanEdit(own, writableIds));

        // R-78: the shared delivery templates are the fallback of an organization that defines none
        // of its own; a name the user's organization defines is its own template's (the resolver's rule).
        // Their usage counts are the platform organization's, not the reader's: left out.
        var ownNames = own.Select(template => template.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Ok(WithCanEdit(templates
            .Where(template => accessibleIds.Contains(template.Id)
                || template.IsShared && !ownNames.Contains(template.Name))
            .Select(template => accessibleIds.Contains(template.Id)
                ? template
                : template with { PipelineCount = 0, LatestRunAt = null })
            .ToList(), writableIds));
    }

    /// <summary>Marks each template the caller may change: Write on it, and a site Admin for a shared
    /// delivery template (the rule <see cref="CanChangeTemplateAsync"/> enforces on save and delete).</summary>
    private List<PipelineTemplateSummaryDto> WithCanEdit(
        List<PipelineTemplateSummaryDto> templates, IReadOnlyCollection<int>? writableIds) =>
        templates.Select(template => template with
        {
            CanEdit = (writableIds is null || writableIds.Contains(template.Id))
                && (User.IsInRole("Admin") || !template.IsShared)
        }).ToList();

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PipelineTemplateDto>> GetTemplate(int id, CancellationToken ct)
    {
        if (!await CanReadAsync(id, ct)) return Forbid();
        var template = await templateService.GetTemplateAsync(id, ct);
        if (template is null) return NotFound();
        return Ok(template with
        {
            IsShared = await templateService.IsSharedCatalogTemplateAsync(id, ct),
            CanEdit = await CanChangeTemplateAsync(id, ct)
        });
    }

    [HttpGet("{id:int}/versions")]
    public async Task<ActionResult<PaginatedResult<PipelineTemplateVersionSummaryDto>>> GetTemplateVersions(
        int id, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await CanReadAsync(id, ct)) return Forbid();
        return Ok(await templateService.GetTemplateVersionsAsync(id, request, ct));
    }

    [HttpGet("{id:int}/versions/{version:int}")]
    public async Task<ActionResult<PipelineTemplateVersionDto>> GetTemplateVersion(
        int id, int version, CancellationToken ct)
    {
        if (!await CanReadAsync(id, ct)) return Forbid();
        var item = await templateService.GetTemplateVersionAsync(id, version, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<PipelineTemplateDto>> CreateTemplate(
        [FromBody] CreatePipelineTemplateRequest request, CancellationToken ct)
    {
        if (!await HasPermissionAsync(null, Permission.Write, ct)) return Forbid();
        var (allowed, organizationId) = await ResolveCreationOrganizationAsync(request.OrganizationId, ct);
        if (!allowed || !await MayHoldNameAsync(request.Name, organizationId, ct)) return Forbid();
        var template = await templateService.CreateTemplateAsync(
            request with { OrganizationId = organizationId }, ct);
        return CreatedAtAction(nameof(GetTemplate), new { id = template.Id }, template);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PipelineTemplateDto>> UpdateTemplate(
        int id, [FromBody] UpdatePipelineTemplateRequest request, CancellationToken ct)
    {
        if (!await CanChangeTemplateAsync(id, ct)) return Forbid();
        var template = await templateService.UpdateTemplateAsync(id, request, ct);
        if (template is null) return NotFound();
        return Ok(template);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteTemplate(int id, CancellationToken ct)
    {
        if (!await CanChangeTemplateAsync(id, ct)) return Forbid();
        var deleted = await templateService.DeleteTemplateAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpGet("{id:int}/export")]
    public async Task<IActionResult> ExportTemplate(int id, CancellationToken ct)
    {
        if (!await CanReadAsync(id, ct)) return Forbid();
        var template = await templateService.GetTemplateAsync(id, ct);
        if (template is null) return NotFound();
        var bytes = System.Text.Encoding.UTF8.GetBytes(template.YamlContent);
        return File(bytes, "application/x-yaml", $"{template.Name}.yaml");
    }

    [HttpPost("import")]
    public async Task<ActionResult<PipelineTemplateDto>> ImportTemplate(IFormFile file, CancellationToken ct)
    {
        if (!await HasPermissionAsync(null, Permission.Write, ct)) return Forbid();
        var (allowed, organizationId) = await ResolveCreationOrganizationAsync(null, ct);
        if (!allowed) return Forbid();
        if (file.Length == 0 || file.Length > 1_048_576) return BadRequest(new ApiError { Message = "File is empty or exceeds 1 Mo." });

        using var reader = new StreamReader(file.OpenReadStream());
        var yaml = await reader.ReadToEndAsync(ct);
        if (PipelineTemplateService.ReadImportedTemplateName(yaml).Name is { } importedName
            && !await MayHoldNameAsync(importedName, organizationId, ct))
            return Forbid();

        var template = await templateService.ImportTemplateAsync(yaml, ct, organizationId);
        if (template is null) return BadRequest(new ApiError { Message = "Invalid YAML pipeline definition." });
        return CreatedAtAction(nameof(GetTemplate), new { id = template.Id }, template);
    }

    [HttpPost("{id:int}/resolve")]
    public async Task<ActionResult<string>> ResolveTemplate(
        int id,
        [FromBody, BoundedDictionary(64, 200, 4000)] Dictionary<string, string>? parameters,
        CancellationToken ct,
        [FromQuery] int? version = null)
    {
        if (!await HasPermissionAsync(id, Permission.Read, ct)) return Forbid();
        var template = await templateService.GetTemplateAsync(id, ct);
        if (template is null) return NotFound();

        var yaml = version is null
            ? template.YamlContent
            : (await templateService.GetTemplateVersionAsync(id, version.Value, ct))?.YamlContent;
        if (yaml is null) return NotFound();
        var resolved = await templateService.ResolveTemplateAsync(
            yaml, parameters, ct, template.OrganizationId);
        if (resolved is null) return BadRequest(new ApiError { Message = "Failed to resolve template." });
        return Ok(resolved);
    }

    private Task<bool> HasPermissionAsync(int? id, Permission permission, CancellationToken ct) =>
        authz.HasPermissionAsync(User, ResourceType.PipelineTemplate, id, permission, ct);

    /// <summary>Read on the template, or a shared delivery template (read only, R-78).</summary>
    private Task<bool> CanReadAsync(int id, CancellationToken ct) =>
        PipelineTemplateReadRule.CanReadAsync(authz, templateService, User, id, ct);

    /// <summary>A shipped catalog template of the platform organization runs in every organization
    /// that extends it (with that organization's secrets), so only a site Admin may change it.</summary>
    private async Task<bool> CanChangeTemplateAsync(int id, CancellationToken ct) =>
        await HasPermissionAsync(id, Permission.Write, ct)
        && (User.IsInRole("Admin") || !await templateService.IsSharedCatalogTemplateAsync(id, ct));

    /// <summary>A catalog name (any case) created in the platform organization would be resolved by
    /// every organization as the shared template: a site Admin's decision.</summary>
    private async Task<bool> MayHoldNameAsync(string name, int? organizationId, CancellationToken ct) =>
        User.IsInRole("Admin")
        || organizationId is not { } resolved
        || !await templateService.IsSharedCatalogNameAsync(name, resolved, ct);

    private async Task<(bool Allowed, int? OrganizationId)> ResolveCreationOrganizationAsync(
        int? organizationId, CancellationToken ct)
    {
        if (User.IsInRole("Admin")) return (true, organizationId);
        // ADR-059: a template goes only to an organization whose role grants template Write.
        var organizationIds = await authz.GetOrganizationIdsWithPermissionAsync(
            User, ResourceType.PipelineTemplate, Permission.Write, ct);
        var resolved = organizationId ?? organizationIds.FirstOrDefault();
        return resolved > 0 && organizationIds.Contains(resolved)
            ? (true, resolved)
            : (false, null);
    }
}
