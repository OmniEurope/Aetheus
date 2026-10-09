// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// Table by table, after the administrator confirmed it: refused while a pipeline runs (the rewrite locks
/// the table the runs write their telemetry and analyses to), measured before and after, written to the
/// audit log at the start and at the end. Nothing schedules it: only the endpoint calls it.
/// </summary>
public sealed class ReclaimSpaceService(
    IReclaimSpaceRepository repository,
    ReclaimSpaceTracker tracker,
    IServiceScopeFactory scopeFactory,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<ReclaimSpaceService> logger) : IReclaimSpaceService
{
    public async Task<ReclaimSpaceStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var tables = new List<ReclaimSpaceTableDto>();
        foreach (var table in ReclaimSpaceRepository.Statements.Keys)
            tables.Add(new ReclaimSpaceTableDto(table, await repository.GetTableBytesAsync(table, ct).ConfigureAwait(false)));
        return new ReclaimSpaceStatusDto
        {
            ActivePipelineRuns = await repository.CountActiveRunsAsync(ct).ConfigureAwait(false),
            RunningTable = tracker.RunningTable,
            Tables = [.. tables.OrderByDescending(table => table.Bytes ?? -1)],
            History = tracker.History
        };
    }

    public async Task<ReclaimSpaceResultDto> StartAsync(string table, string requestedBy, CancellationToken ct = default)
    {
        if (!ReclaimSpaceRepository.Statements.ContainsKey(table))
            throw new BadRequestException("Space can only be reclaimed from a table the data retention purges.");
        var started = new ReclaimSpaceResultDto
        {
            Table = table,
            RequestedBy = requestedBy,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime
        };
        if (!tracker.TryBegin(started))
            throw new ConflictException($"Space is already being reclaimed from {tracker.RunningTable}.");
        try
        {
            var activeRuns = await repository.CountActiveRunsAsync(ct).ConfigureAwait(false);
            if (activeRuns > 0)
                throw new ConflictException(string.Create(CultureInfo.InvariantCulture,
                    $"{activeRuns} pipeline runs are in progress; reclaim the space once they have ended."));
            started = started with { BytesBefore = await repository.GetTableBytesAsync(table, ct).ConfigureAwait(false) };
        }
        catch
        {
            // Mandatory: a refused start gives the slot back before the refusal reaches the caller.
            tracker.Abandon();
            throw;
        }

        await audit.LogAsync("ReclaimSpaceStarted", "Database", null,
            string.Create(CultureInfo.InvariantCulture, $"table={table}; before={started.BytesBefore} bytes"), ct).ConfigureAwait(false);
        // The rewrite outlives the request: its own scope, no request token.
        tracker.Track(Task.Run(() => RunAsync(started)));
        return started;
    }

    private async Task RunAsync(ReclaimSpaceResultDto started)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var scopedRepository = scope.ServiceProvider.GetRequiredService<IReclaimSpaceRepository>();
        ReclaimSpaceResultDto result;
        try
        {
            await scopedRepository.VacuumFullAsync(started.Table).ConfigureAwait(false);
            result = started with
            {
                BytesAfter = await scopedRepository.GetTableBytesAsync(started.Table).ConfigureAwait(false),
                CompletedAt = timeProvider.GetUtcNow().UtcDateTime,
                Succeeded = true
            };
        }
        catch (Exception exception)
        {
            // Mandatory: a failed rewrite is reported on the page and in the audit, never lost.
            logger.LogError(exception, "Reclaiming the space of {Table} failed", started.Table);
            result = started with { CompletedAt = timeProvider.GetUtcNow().UtcDateTime, Error = exception.Message };
        }
        tracker.Complete(result);
        await scope.ServiceProvider.GetRequiredService<IAuditService>().LogAsync(
            result.Succeeded ? "ReclaimSpaceCompleted" : "ReclaimSpaceFailed", "Database", null,
            string.Create(CultureInfo.InvariantCulture,
                $"table={result.Table}; requested by={result.RequestedBy}; before={result.BytesBefore} bytes; after={result.BytesAfter} bytes{(result.Error is null ? string.Empty : "; error=" + result.Error)}"))
            .ConfigureAwait(false);
    }
}
