// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

[ApiController]
[Route("api/pipelines")]
[Authorize]
public sealed class PipelineHolderDeclarationController(
    IPipelineService pipelineService,
    IPipelineHolderDeclarationService declarations,
    IPipelineOwnerAuthorization ownerAuthz,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>R-24: declares a vault or a variable library of the pipeline's project in the pipeline's
    /// definition (<c>vaults:</c> or <c>variable_libraries:</c>), the one edit that makes a key that holder already
    /// holds readable by its runs. The rights are those of <c>PUT api/pipelines/{id}</c>, which writes that same
    /// YAML (write on the pipeline, write on pipelines in the organization of its project, the rights over its
    /// owner), plus Write on the pipeline's project and Read on the holder, since the pipeline's runs will read it.
    /// A holder already declared changes nothing and answers the pipeline as it is.</summary>
    [HttpPost("{id:int}/declared-holders")]
    public async Task<ActionResult<PipelineDto>> DeclareHolder(
        int id, [FromBody] DeclarePipelineHolderRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Write, ct))
            return Forbid();

        var existing = await pipelineService.GetPipelineAsync(id, ct);
        if (existing is null) return NotFound();
        if (existing.ProjectId is not { } projectId
            || !await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Write, ct)
            || !await authz.CanCreateInProjectAsync(User, ResourceType.Pipeline, existing.ProjectId, ct)
            || !await ownerAuthz.CanOwnAsync(User, existing.ProjectId, existing.EnvironmentId, existing.ProjectServerId, ct))
            return Forbid();

        var declaration = await declarations.PrepareAsync(existing, request, ct);
        var holderType = declaration.Kind == PipelineSetupReadinessContainerKind.Vault ? ResourceType.Vault : ResourceType.VariableLibrary;
        if (!await authz.HasPermissionAsync(User, holderType, declaration.HolderId, Permission.Read, ct))
            return Forbid();

        return Ok(await declarations.ApplyAsync(existing, declaration, ct));
    }
}
