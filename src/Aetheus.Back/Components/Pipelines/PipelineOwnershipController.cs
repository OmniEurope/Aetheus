// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Administrator-only view and transfer of pipeline owners.</summary>
[ApiController]
[Route("api/pipelines/ownership")]
[Authorize(Roles = "Admin")]
public sealed class PipelineOwnershipController(IPipelineOwnershipService ownership) : ControllerBase
{
    [HttpGet("owned")]
    public async Task<ActionResult<List<PipelineOwnershipDto>>> GetOwned(
        [FromQuery, Required, StringLength(50)] string owner, CancellationToken ct) =>
        Ok(await ownership.GetOwnedPipelinesAsync(owner, ct));

    [HttpGet("candidates")]
    public async Task<ActionResult<List<PipelineOwnerCandidateDto>>> GetCandidates(CancellationToken ct) =>
        Ok(await ownership.GetCandidatesAsync(ct));

    [HttpPost("preview")]
    public async Task<ActionResult<PipelineOwnerTransferResultDto>> Preview(
        [FromBody] PipelineOwnerTransferRequest request, CancellationToken ct)
    {
        var result = await ownership.PreviewAsync(User, request, ct);
        return result is null ? Forbid() : Ok(result);
    }

    [HttpPost("transfer")]
    public async Task<ActionResult<PipelineOwnerTransferResultDto>> Transfer(
        [FromBody] PipelineOwnerTransferRequest request, CancellationToken ct)
    {
        var result = await ownership.TransferAsync(User, request, ct);
        return result is null ? Forbid() : Ok(result);
    }
}
