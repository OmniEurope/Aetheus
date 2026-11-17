// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-26: the setup wizard prepares the collection of the project's monitored application (<see cref="IPipelineSetupCollectionService"/>).
/// It sits beside <see cref="PipelineSetupController"/>, which is already at its size budget.
/// </summary>
[ApiController]
[Route("api/pipelines/setup")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public class PipelineSetupCollectionController(
    IPipelineSetupCollectionService collection,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>Prepares the telemetry and the audience the wizard was asked for, idempotently, item by item. Gated on WRITE
    /// access to the project, the permission the monitoring settings it goes through need.</summary>
    [HttpPost("collection")]
    public async Task<ActionResult<PipelineSetupCollectionResultDto>> Prepare(
        [FromBody] PipelineSetupCollectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, request.ProjectId, Permission.Write, ct))
            return Forbid();

        var result = await collection.PrepareAsync(request, ct);
        return result is null ? NotFound() : Ok(result);
    }
}
