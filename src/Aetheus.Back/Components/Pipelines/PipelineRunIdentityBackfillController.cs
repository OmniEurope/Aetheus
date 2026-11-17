// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>ADR-060: how far the run backfill went, so its end is proved without SSH before the column
/// turns NOT NULL. Counts every organization's runs, hence administrators only.</summary>
[ApiController]
[Route("api/admin/pipeline-run-identity-backfill")]
[Authorize(Roles = "Admin")]
public class PipelineRunIdentityBackfillController(IPipelineRunIdentityProgressService progress) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PipelineRunIdentityBackfillDto>> Get(CancellationToken ct) =>
        Ok(await progress.GetAsync(ct));
}
