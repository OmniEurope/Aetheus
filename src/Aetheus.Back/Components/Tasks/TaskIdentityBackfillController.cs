// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http;

namespace Aetheus.Back.Components.Tasks;

/// <summary>ADR-060: how far the task backfill went, so its end is proved from the interface. Counts every
/// organization's tasks, hence administrators only, as the Performance page that shows it.</summary>
[ApiController]
[Route("api/admin/task-identity-backfill")]
[Authorize(Roles = "Admin")]
// Read by the Performance page, whose own traffic must not announce a change of itself.
[DisableHttpMetrics]
public class TaskIdentityBackfillController(ITaskService taskService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TaskIdentityBackfillDto>> Get(CancellationToken ct) =>
        Ok(await taskService.GetIdentityBackfillProgressAsync(ct));
}
