// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The expand/contract tracking of the database as an environment shows it (recette R-18): the contractions still due,
/// and the last gate verdict of a migrate step that ran on its servers. Read by those who may read the environment;
/// the verdict belongs to a pipeline run, so it is left out for those who may not also read that pipeline.
/// </summary>
[ApiController]
[Route("api/environments")]
[Authorize]
public sealed class EnvironmentSchemaTrackingController(
    IPipelineRunService runService,
    IPipelineSchemaTrackingService tracking,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>The pending contractions and the last gate verdict of a migrate step that ran on the environment's servers.</summary>
    [HttpGet("{environmentId:int}/schema-tracking")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EnvironmentSchemaDto>> GetEnvironmentSchemaTracking(int environmentId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Environment, environmentId, Permission.Read, ct)) return Forbid();
        var schema = await tracking.GetForEnvironmentAsync(environmentId, ct);
        if (schema is null) return NotFound();
        return Ok(schema.LastGate is { } gate && !await CanReadPipelineOfRunAsync(gate.RunId, ct)
            ? schema with { LastGate = null }
            : schema);
    }

    private async Task<bool> CanReadPipelineOfRunAsync(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        return pipelineId is not null
            && await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct);
    }
}
