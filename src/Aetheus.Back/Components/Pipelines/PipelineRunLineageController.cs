// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The lineage tile of a run's page, read by those who may read its pipeline.</summary>
[ApiController]
[Route("api/pipelines")]
[Authorize]
public sealed class PipelineRunLineageController(
    IPipelineRunService runService,
    IPipelineRunLineageService lineageService,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>Who launched the run and which runs it started.</summary>
    [HttpGet("runs/{runId:int}/lineage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PipelineRunLineageDto>> GetRunLineage(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct)) return Forbid();
        var lineage = await lineageService.GetLineageAsync(runId, ct);
        if (lineage is null) return NotFound();
        // A run this one started may belong to a pipeline the caller cannot read: it is left out, so the
        // tile names no pipeline, project or build the caller could not open. Null means every pipeline.
        var readable = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct);
        return Ok(readable is null
            ? lineage
            : lineage with { Downstream = [.. lineage.Downstream.Where(link => readable.Contains(link.PipelineId))] });
    }
}
