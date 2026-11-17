// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http;

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>R-6: the real volume of each table of the database, read from the catalog, so the tables
/// that may move to UUID v7 are chosen from production figures without SSH. Site administrators
/// only: table names and sizes are not organization-scoped.</summary>
[ApiController]
[Route("api/admin/database-tables")]
[Authorize(Roles = "Admin")]
public class DatabaseTableVolumeController(IDatabaseTableVolumeService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<DatabaseTableVolumeDto>>> Get(CancellationToken ct) =>
        Ok(await service.ListAsync(ct));
}
