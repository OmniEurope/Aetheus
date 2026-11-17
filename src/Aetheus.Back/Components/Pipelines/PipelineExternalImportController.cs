// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-11 (decision of 2026-11-10, option A): the pipelines of a project's external repository mirrors, imported
/// read-only. Listing needs READ on the project. Importing creates (or replaces the definition of) a pipeline of the
/// project, so it takes exactly the checks of a pipeline creation (<c>POST /api/pipelines</c>): Pipeline WRITE in the
/// global RBAC, the ADR-059 organization rule for a pipeline of that project, and the right to own a pipeline of
/// that project. All are checked here, server-side, before the service reads anything.
/// </summary>
[ApiController]
[Route("api/projects/{projectId:int}/external-pipelines")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public class PipelineExternalImportController(
    IPipelineExternalImportService imports,
    IResourceAuthorizationService authz,
    IPipelineOwnerAuthorization ownerAuthz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ExternalPipelineCandidatesDto>> List(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await imports.ListAsync(projectId, ct));
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PipelineDto>> Import(
        int projectId, [FromBody] ImportExternalPipelineRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The checks of PipelinesController.CreatePipeline, in the same order, for a pipeline owned by the project.
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, null, Permission.Write, ct))
            return Forbid();
        if (!await authz.CanCreateInProjectAsync(User, ResourceType.Pipeline, projectId, ct))
            return Forbid();
        if (!await ownerAuthz.CanOwnAsync(User, projectId, null, null, ct))
            return Forbid();
        var actor = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor)) return Forbid();
        return Ok(await imports.ImportAsync(projectId, request, actor, ct));
    }
}
