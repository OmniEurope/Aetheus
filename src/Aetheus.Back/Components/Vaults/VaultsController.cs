// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Vaults;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class VaultsController(IVaultService service, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<VaultDto>>> GetVaults(
        [FromQuery] int? projectId, [FromQuery] int? environmentId, [FromQuery] int? projectServerId,
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Vault, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<VaultDto>());
        return Ok(await service.GetVaultsAsync(projectId, environmentId, projectServerId, request, accessibleIds, ct));
    }

    /// <summary>The project names the vaults list's column filter offers.</summary>
    [HttpGet("filter-values")]
    public async Task<ActionResult<VaultFilterValuesDto>> GetFilterValues(CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Vault, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new VaultFilterValuesDto());
        return Ok(await service.GetFilterValuesAsync(accessibleIds, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<VaultDetailDto>> GetVault(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Read, ct))
            return Forbid();

        var vault = await service.GetVaultDetailAsync(id, ct);
        if (vault is null) return NotFound();
        return Ok(vault);
    }

    [HttpGet("names")]
    public async Task<ActionResult<List<string>>> GetVaultNames([FromQuery] int? projectId, CancellationToken ct)
    {
        // RBAC audit, finding 5: the project named must be one the caller reads, and a caller without
        // any readable vault gets nothing rather than a list cached for a wider caller.
        if (projectId is not null
            && !await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Vault, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new List<string>());
        return Ok(await service.GetVaultNamesAsync(projectId, accessibleIds, ct));
    }

    [HttpPost]
    public async Task<ActionResult<VaultDto>> CreateVault([FromBody] CreateVaultRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, null, Permission.Write, ct))
            return Forbid();
        // ADR-059: the owner named (project, environment or project server) is one the caller writes,
        // in an organization that lets it create vaults (RBAC audit, findings 1 and 8).
        if (!await authz.CanOwnAsync(User, ResourceType.Vault, request.ProjectId, request.EnvironmentId, request.ProjectServerId, ct))
            return Forbid();

        var vault = await service.CreateVaultAsync(request, ct);
        return CreatedAtAction(nameof(GetVault), new { id = vault.Id }, vault);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<VaultDto>> UpdateVault(int id, [FromBody] UpdateVaultRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Write, ct))
            return Forbid();
        // The owner the resource ends up with must also let the caller write (no move out of reach).
        if (!await authz.CanOwnAsync(User, ResourceType.Vault, request.ProjectId, request.EnvironmentId, request.ProjectServerId, ct))
            return Forbid();

        var vault = await service.UpdateVaultAsync(id, request, ct);
        if (vault is null) return NotFound();
        return Ok(vault);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteVault(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Admin, ct))
            return Forbid();

        var deleted = await service.DeleteVaultAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/secrets")]
    public async Task<ActionResult<VaultSecretDto>> CreateSecret(int id, [FromBody] CreateVaultSecretRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Write, ct))
            return Forbid();

        var secret = await service.CreateSecretAsync(id, request, ct);
        return Created($"api/vaults/{id}/secrets/{secret.Id}", secret);
    }

    [HttpPut("{id:int}/secrets/{secretId:int}")]
    public async Task<ActionResult<VaultSecretDto>> UpdateSecret(int id, int secretId, [FromBody] UpdateVaultSecretRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Write, ct))
            return Forbid();

        var secret = await service.UpdateSecretAsync(id, secretId, request, ct);
        if (secret is null) return NotFound();
        return Ok(secret);
    }

    [HttpPost("{id:int}/secrets/{secretId:int}/rotate")]
    public async Task<ActionResult<VaultSecretDto>> RotateSecret(int id, int secretId, [FromBody] RotateVaultSecretRequest request, CancellationToken ct)
    {
        // Rotation == new value + new versioned audit entry tagged Rotated. Same RBAC as Write.
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Write, ct))
            return Forbid();

        var secret = await service.RotateSecretAsync(id, secretId, request, ct);
        if (secret is null) return NotFound();
        return Ok(secret);
    }

    [HttpDelete("{id:int}/secrets/{secretId:int}")]
    public async Task<IActionResult> DeleteSecret(int id, int secretId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Write, ct))
            return Forbid();

        var deleted = await service.DeleteSecretAsync(id, secretId, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    /// <summary> The clear value of one secret, for the copy button. POST so no URL, proxy log or
    /// browser history ever carries it; its own permission (R-11, Contributor by default), never
    /// Read; no-store so no cache keeps it; audited by the service. </summary>
    [HttpPost("{id:int}/secrets/{secretId:int}/reveal")]
    public async Task<ActionResult<RevealedSecretValueDto>> RevealSecretValue(int id, int secretId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.VaultSecretReveal, id, ct))
            return Forbid();

        var value = await service.RevealSecretValueAsync(id, secretId, ct);
        if (value is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Ok(new RevealedSecretValueDto { Value = value });
    }

    [HttpGet("{id:int}/secrets/{secretId:int}/versions")]
    public async Task<ActionResult<List<VaultSecretVersionDto>>> GetSecretVersions(int id, int secretId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Read, ct))
            return Forbid();

        return Ok(await service.GetSecretVersionsAsync(id, secretId, ct));
    }

    [HttpGet("{id:int}/export-keys")]
    public async Task<ActionResult<List<string>>> ExportSecretKeys(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Read, ct))
            return Forbid();

        return Ok(await service.ExportSecretKeysAsync(id, ct));
    }

    [HttpPost("{id:int}/import")]
    public async Task<ActionResult<ImportResultDto>> ImportSecrets(int id, [FromBody] List<CreateVaultSecretRequest> secrets, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Vault, id, Permission.Write, ct))
            return Forbid();

        // Hardening (#17): cap bulk import to prevent DoS via outsized payloads.
        const int maxImportEntries = 200;
        if (secrets is null || secrets.Count == 0)
            return BadRequest(new { error = "Empty", message = "No secrets supplied." });
        if (secrets.Count > maxImportEntries)
            return BadRequest(new { error = "TooMany", message = $"At most {maxImportEntries} secrets per import." });

        var count = await service.ImportSecretsAsync(id, secrets, ct);
        return Ok(new ImportResultDto { ImportedCount = count });
    }
}
