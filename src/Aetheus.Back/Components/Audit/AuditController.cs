// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Components.Audit;

[ApiController]
[Route("api/audit")]
[Authorize(Roles = "Admin")]
public class AuditController(IAuditService auditService, IAuditChainService chainService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<AuditLogDto>>> GetAuditLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery, StringLength(200)] string? search = null,
        [FromQuery, StringLength(100)] string? action = null,
        [FromQuery, StringLength(100)] string? entityType = null,
        [FromQuery] int? entityId = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery, StringLength(50)] string? sortBy = null,
        [FromQuery] bool sortDescending = true,
        CancellationToken ct = default,
        [FromQuery(Name = "Filters"), MaxLength(PaginationRequest.MaxFilters)] List<GridFilter>? filters = null)
    {
        page = Math.Max(1, page);
        pageSize = PaginationDefaults.Clamp(pageSize);
        // The grid's header filters travel as the generic column filters, beside the typed
        // parameters other callers still send.
        return Ok(await auditService.GetLogsPagedAsync(page, pageSize, search, action, entityType, entityId, dateFrom, dateTo, ct,
            sortBy, sortDescending, filters is { Count: > 0 } ? filters : null));
    }

    [HttpGet("actions")]
    public async Task<ActionResult<List<string>>> GetActions(CancellationToken ct = default)
    {
        return Ok(await auditService.GetDistinctActionsAsync(ct));
    }

    [HttpGet("entity-types")]
    public async Task<ActionResult<List<string>>> GetEntityTypes(CancellationToken ct = default)
    {
        return Ok(await auditService.GetDistinctEntityTypesAsync(ct));
    }

    [HttpGet("verify-chain")]
    public async Task<ActionResult<AuditChainVerificationResult>> VerifyChain(CancellationToken ct = default)
    {
        return Ok(await chainService.VerifyChainAsync(ct));
    }

    [HttpGet("{id:int}/verify")]
    public async Task<ActionResult<AuditChainVerificationResult>> VerifyEntry(int id, CancellationToken ct = default)
    {
        var result = await chainService.VerifyUpToEntryAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }
}
