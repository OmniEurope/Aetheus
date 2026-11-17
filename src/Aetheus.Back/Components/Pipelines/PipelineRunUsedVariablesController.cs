// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The libraries and vaults a run read, variable by variable, for the run page's parameters dialog. Read
/// by those who may read the run's pipeline (the same rule as the run itself); a vault secret's value never leaves.</summary>
[ApiController]
[Route("api/pipelines")]
[Authorize]
public sealed class PipelineRunUsedVariablesController(
    IPipelineRunService runService,
    IPipelineRunUsedVariablesService usedVariables,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>Each variable the run read from a library or a vault, with the source that holds it.</summary>
    [HttpGet("runs/{runId:int}/used-variables")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<PipelineRunUsedVariableDto>>> GetUsedVariables(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct)) return Forbid();
        var variables = await usedVariables.GetUsedVariablesAsync(runId, ct);
        return variables is null ? NotFound() : Ok(variables);
    }
}
