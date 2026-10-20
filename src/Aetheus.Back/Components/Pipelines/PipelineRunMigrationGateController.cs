// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The expand/contract gate verdicts of a run (recette R1-43), read by those who may read its pipeline.</summary>
[ApiController]
[Route("api/pipelines")]
[Authorize]
public sealed class PipelineRunMigrationGateController(
    IPipelineRunService runService,
    IPipelineMigrationGateService gateService,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>Every gate verdict the run's migrate steps reported, oldest first; empty when none ran.</summary>
    [HttpGet("runs/{runId:int}/migration-gate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<PipelineRunMigrationGateDto>>> GetRunMigrationGate(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct)) return Forbid();
        return Ok(await gateService.GetForRunAsync(runId, ct));
    }
}
