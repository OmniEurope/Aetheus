// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Lists pending approvals across readable pipelines for the home page and top
/// bar.</summary>
[ApiController]
[Route("api/pipelines/approvals")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
public class PipelineApprovalsController(
    IPipelineApprovalService approvalService,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<List<PendingApprovalDto>>> GetPending(CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new List<PendingApprovalDto>());
        return Ok(await approvalService.GetPendingApprovalsAsync(accessibleIds, ct));
    }
}
