// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The expand/contract tracking of a run (recette R-18): what its gate concluded about the migrations it applied,
/// and the contractions still due. Read by those who may read its pipeline, like the gate verdicts themselves.
/// </summary>
[ApiController]
[Route("api/pipelines/runs")]
[Authorize]
public sealed class PipelineRunSchemaTrackingController(
    IPipelineRunService runService,
    IPipelineSchemaTrackingService tracking,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>The run's gate verdicts, its migrations classified expand or contract, and the pending contractions.</summary>
    [HttpGet("{runId:int}/schema-tracking")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PipelineRunSchemaDto>> GetRunSchemaTracking(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct)) return Forbid();
        return Ok(await tracking.GetForRunAsync(runId, ct));
    }
}
