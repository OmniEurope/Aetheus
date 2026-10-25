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
        return Ok(accessibleIds is null
            ? templates
            : templates.Where(template => accessibleIds.Contains(template.Id)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PipelineTemplateDto>> GetTemplate(int id, CancellationToken ct)
    {
        if (!await HasPermissionAsync(id, Permission.Read, ct)) return Forbid();
        var template = await templateService.GetTemplateAsync(id, ct);
        if (template is null) return NotFound();
        return Ok(template);
    }

    [HttpGet("{id:int}/versions")]
    public async Task<ActionResult<PaginatedResult<PipelineTemplateVersionSummaryDto>>> GetTemplateVersions(
        int id, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await HasPermissionAsync(id, Permission.Read, ct)) return Forbid();
        return Ok(await templateService.GetTemplateVersionsAsync(id, request, ct));
    }

    [HttpGet("{id:int}/versions/{version:int}")]
    public async Task<ActionResult<PipelineTemplateVersionDto>> GetTemplateVersion(
        int id, int version, CancellationToken ct)
    {
        if (!await HasPermissionAsync(id, Permission.Read, ct)) return Forbid();
        var item = await templateService.GetTemplateVersionAsync(id, version, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<PipelineTemplateDto>> CreateTemplate(
        [FromBody] CreatePipelineTemplateRequest request, CancellationToken ct)
    {
        if (!await HasPermissionAsync(null, Permission.Write, ct)) return Forbid();
        var (allowed, organizationId) = await ResolveCreationOrganizationAsync(request.OrganizationId, ct);
        if (!allowed) return Forbid();
        var template = await templateService.CreateTemplateAsync(
            request with { OrganizationId = organizationId }, ct);
        return CreatedAtAction(nameof(GetTemplate), new { id = template.Id }, template);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PipelineTemplateDto>> UpdateTemplate(
        int id, [FromBody] UpdatePipelineTemplateRequest request, CancellationToken ct)
    {
        if (!await HasPermissionAsync(id, Permission.Write, ct)) return Forbid();
        var template = await templateService.UpdateTemplateAsync(id, request, ct);
        if (template is null) return NotFound();
        return Ok(template);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteTemplate(int id, CancellationToken ct)
    {
        if (!await HasPermissionAsync(id, Permission.Write, ct)) return Forbid();
        var deleted = await templateService.DeleteTemplateAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpGet("{id:int}/export")]
    public async Task<IActionResult> ExportTemplate(int id, CancellationToken ct)
    {
        if (!await HasPermissionAsync(id, Permission.Read, ct)) return Forbid();
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

    private async Task<(bool Allowed, int? OrganizationId)> ResolveCreationOrganizationAsync(
        int? organizationId, CancellationToken ct)
    {
        if (User.IsInRole("Admin")) return (true, organizationId);
        var organizationIds = await authz.GetUserOrganizationIdsAsync(User, ct);
        var resolved = organizationId ?? organizationIds.FirstOrDefault();
        return resolved > 0 && organizationIds.Contains(resolved)
            ? (true, resolved)
            : (false, null);
    }
}
