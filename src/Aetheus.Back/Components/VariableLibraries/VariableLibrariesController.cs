// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.VariableLibraries;

[ApiController]
[Route("api/variable-libraries")]
[Authorize]
public class VariableLibrariesController(IVariableLibraryService service, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<VariableLibraryDto>>> GetLibraries(
        [FromQuery] int? projectId, [FromQuery] int? environmentId, [FromQuery] int? projectServerId,
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.VariableLibrary, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<VariableLibraryDto>());
        return Ok(await service.GetLibrariesAsync(projectId, environmentId, projectServerId, request, accessibleIds, ct));
    }

    /// <summary>The project names the variable libraries list's column filter offers.</summary>
    [HttpGet("filter-values")]
    public async Task<ActionResult<VariableLibraryFilterValuesDto>> GetFilterValues(CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.VariableLibrary, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new VariableLibraryFilterValuesDto());
        return Ok(await service.GetFilterValuesAsync(accessibleIds, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<VariableLibraryDetailDto>> GetLibrary(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Read, ct))
            return Forbid();

        var library = await service.GetLibraryDetailAsync(id, ct);
        if (library is null) return NotFound();
        return Ok(library);
    }

    [HttpGet("names")]
    public async Task<ActionResult<List<string>>> GetLibraryNames([FromQuery] int? projectId, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.VariableLibrary, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new List<string>());
        return Ok(await service.GetLibraryNamesAsync(projectId, accessibleIds, ct));
    }

    [HttpGet("suggestion-keys")]
    public async Task<ActionResult<PaginatedResult<string>>> GetSuggestionKeys(
        [FromQuery] int? projectId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.VariableLibrary, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<string>());
        return Ok(await service.GetSuggestionKeysAsync(projectId, request, accessibleIds, ct));
    }

    [HttpPost]
    public async Task<ActionResult<VariableLibraryDto>> CreateLibrary([FromBody] CreateVariableLibraryRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, null, Permission.Write, ct))
            return Forbid();

        var library = await service.CreateLibraryAsync(request, ct);
        return CreatedAtAction(nameof(GetLibrary), new { id = library.Id }, library);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<VariableLibraryDto>> UpdateLibrary(int id, [FromBody] UpdateVariableLibraryRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Write, ct))
            return Forbid();

        var library = await service.UpdateLibraryAsync(id, request, ct);
        if (library is null) return NotFound();
        return Ok(library);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteLibrary(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Admin, ct))
            return Forbid();

        var deleted = await service.DeleteLibraryAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/entries")]
    public async Task<ActionResult<VariableEntryDto>> CreateEntry(int id, [FromBody] CreateVariableEntryRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Write, ct))
            return Forbid();

        var entry = await service.CreateEntryAsync(id, request, ct);
        return Created($"api/variable-libraries/{id}/entries/{entry.Id}", entry);
    }

    [HttpGet("{id:int}/entries")]
    public async Task<ActionResult<PaginatedResult<VariableEntryDto>>> GetEntries(
        int id, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Read, ct))
            return Forbid();

        return Ok(await service.GetEntriesAsync(id, request, ct));
    }

    [HttpPut("{id:int}/entries/{entryId:int}")]
    public async Task<ActionResult<VariableEntryDto>> UpdateEntry(int id, int entryId, [FromBody] UpdateVariableEntryRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Write, ct))
            return Forbid();

        var entry = await service.UpdateEntryAsync(id, entryId, request, ct);
        if (entry is null) return NotFound();
        return Ok(entry);
    }

    [HttpDelete("{id:int}/entries/{entryId:int}")]
    public async Task<IActionResult> DeleteEntry(int id, int entryId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Write, ct))
            return Forbid();

        var deleted = await service.DeleteEntryAsync(id, entryId, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpGet("{id:int}/entries/{entryId:int}/versions")]
    public async Task<ActionResult<PaginatedResult<VariableEntryVersionDto>>> GetEntryVersions(
        int id, int entryId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Read, ct))
            return Forbid();

        return Ok(await service.GetEntryVersionsAsync(id, entryId, request, ct));
    }

    [HttpGet("{id:int}/export")]
    public async Task<ActionResult<List<VariableEntryDto>>> ExportEntries(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Read, ct))
            return Forbid();

        return Ok(await service.ExportEntriesAsync(id, ct));
    }

    [HttpPost("{id:int}/import")]
    public async Task<ActionResult<ImportResultDto>> ImportEntries(int id, [FromBody] List<CreateVariableEntryRequest> entries, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.VariableLibrary, id, Permission.Write, ct))
            return Forbid();

        var count = await service.ImportEntriesAsync(id, entries, ct);
        return Ok(new ImportResultDto { ImportedCount = count });
    }
}
