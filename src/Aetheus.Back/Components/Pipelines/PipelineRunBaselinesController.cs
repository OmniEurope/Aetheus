// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Returns database-computed average stage and step durations from successful runs without
/// downloading complete run histories.</summary>
[ApiController]
[Route("api/pipelines/{id:int}/runs/{runId:int}/stage-baselines")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
public class PipelineRunBaselinesController(
    IRunStageBaselineService baselines,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<RunStageBaselinesDto>> Get(int id, int runId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Read, ct))
            return Forbid();

        return Ok(await baselines.GetAsync(id, runId, ct));
    }
}
