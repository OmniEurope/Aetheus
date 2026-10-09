// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Audit;

public class AuditService(IAuditRepository repo, IHttpContextAccessor httpContextAccessor, IAuditChainService chainService, IMemoryCache cache, TimeProvider timeProvider, IAdminChangeNotifier? adminNotifier = null) : IAuditService
{
    private static readonly SemaphoreSlim ChainLock = new(1, 1);
    private const int MaxChainRaceRetries = 3;

    public async Task LogAsync(string action, string entityType, int? entityId = null, string? details = null, CancellationToken ct = default)
    {
        var username = httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "system";

        await ChainLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // F-003: Timestamp is set here (AuditLog is exempted from the SaveChangesAsync hook)
            // and the hash is computed BEFORE the single insert, so the row is never UPDATEd -
            // compatible with the DB-level REVOKE UPDATE on AuditLogs (AuditLogAppendOnly).
            // F-014: the in-process lock serializes writers within one instance; across instances
            // the partial unique index on PreviousHash makes a fork impossible - the loser gets a
            // DbUpdateException and retries against the new chain head.
            for (var attempt = 0; ; attempt++)
            {
                var previousHash = await repo.GetLastHashAsync(ct).ConfigureAwait(false);

                var log = new AuditLog
                {
                    Username = username,
                    Action = action,
                    EntityType = entityType,
                    EntityId = entityId,
                    Details = details,
                    // Hashed as stored: PostgreSQL keeps microseconds (SITE-SEC-003).
                    Timestamp = AuditChainService.ToStoredPrecision(timeProvider.GetUtcNow().UtcDateTime),
                    PreviousHash = previousHash
                };
                log.Hash = chainService.ComputeHash(log, previousHash);

                try
                {
                    await repo.AddAsync(log, ct).ConfigureAwait(false);
                    await BroadcastAsync(log.Id, ct).ConfigureAwait(false);
                    return;
                }
                catch (DbUpdateException) when (attempt < MaxChainRaceRetries)
                {
                    // Concurrent instance appended first - re-read the head and retry.
                }
            }
        }
        finally
        {
            ChainLock.Release();
        }
    }

    /// <summary>
    /// Recette R-181: the audit page follows new entries over the admin hub instead of a Refresh
    /// button. Best effort: the entry is written already; a failed push only delays the page until
    /// its next load, so it never fails the audited action.
    /// </summary>
    private async Task BroadcastAsync(int id, CancellationToken ct)
    {
        if (adminNotifier is null) return;
        try
        {
            await adminNotifier.BroadcastAsync(AdminEntities.AuditLog, id, EntityChangeOps.Created, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"[Audit] live push failed: {exception.Message}");
        }
    }

    public async Task<PaginatedResult<AuditLogDto>> GetLogsPagedAsync(int page, int pageSize, string? search = null, string? action = null, string? entityType = null, int? entityId = null, DateTime? dateFrom = null, DateTime? dateTo = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true, IReadOnlyList<GridFilter>? filters = null)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, PaginationRequest.MaxPageSize);
        var skip = (page - 1) * pageSize;
        var totalCount = await repo.CountAsync(search, action, entityType, entityId, dateFrom, dateTo, ct, filters).ConfigureAwait(false);
        var pageItems = await repo.GetPagedAsync(skip, pageSize, search, action, entityType, entityId, dateFrom, dateTo, ct,
            sortBy, sortDescending, filters).ConfigureAwait(false);

        var items = pageItems.Select(l => new AuditLogDto
        {
            Id = l.Id,
            Username = l.Username,
            Action = l.Action,
            EntityType = l.EntityType,
            EntityId = l.EntityId,
            Details = l.Details,
            Timestamp = l.Timestamp
        }).ToList();

        return new PaginatedResult<AuditLogDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<List<string>> GetDistinctActionsAsync(CancellationToken ct = default)
    {
        return await cache.GetOrCreateAsync("audit:distinct:actions", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = BackendRuntimeDefaults.ReferenceDataCacheDuration;
            return await repo.GetDistinctActionsAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? [];
    }

    public async Task<List<string>> GetDistinctEntityTypesAsync(CancellationToken ct = default) =>
        await cache.GetOrCreateAsync("audit:distinct:entityTypes", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = BackendRuntimeDefaults.ReferenceDataCacheDuration;
            return await repo.GetDistinctEntityTypesAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? [];

    public async Task<HashSet<string>> GetRecentLoginIpsAsync(string username, int maxEntries, CancellationToken ct = default)
    {
        return await repo.GetRecentLoginIpsAsync(username, maxEntries, ct).ConfigureAwait(false);
    }
}
