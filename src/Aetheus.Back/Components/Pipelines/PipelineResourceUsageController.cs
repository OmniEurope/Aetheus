// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

[ApiController]
[Route("api/pipelines/resource-usage")]
[Authorize]
public sealed class PipelineResourceUsageController(
    IPipelineResourceUsageService usage,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>Who reads each library and vault the caller can read (recette R-65, R-66).</summary>
    [HttpGet]
    public async Task<ActionResult<ResourceUsageDto>> Get(CancellationToken ct)
    {
        var libraryIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.VariableLibrary, Permission.Read, ct);
        var vaultIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Vault, Permission.Read, ct);
        if (libraryIds is { Count: 0 } && vaultIds is { Count: 0 }) return Ok(new ResourceUsageDto());
        var pipelineIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct);
        return Ok(await usage.GetAsync(libraryIds, vaultIds, pipelineIds, ct));
    }
}
