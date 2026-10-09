// SPDX-License-Identifier: EUPL-1.2
using Npgsql;

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// PLAN-013 lot 6: the one place Aetheus runs <c>VACUUM FULL</c>, an AGENTS.md raw-SQL exception pinned by
/// <c>ReclaimSpaceSqlAuditTests</c>. Each statement is a fixed text per table, never built from input: a
/// table name that is not a key of <see cref="Statements"/> is refused before anything runs. The size
/// reads <c>pg_total_relation_size</c>, the function of the lot 1 measurement.
/// </summary>
public sealed class ReclaimSpaceRepository(AppDbContext db) : IReclaimSpaceRepository
{
    /// <summary>The tables the data retention purges (PLAN-013 lots 2 to 5) and their vacuum statement.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Statements = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ProjectStorageTables.MetricSamples] = "VACUUM (FULL, ANALYZE) \"AppMetricSamples\"",
        [ProjectStorageTables.MetricHourly] = "VACUUM (FULL, ANALYZE) \"AppMetricHourly\"",
        [ProjectStorageTables.Logs] = "VACUUM (FULL, ANALYZE) \"AppLogEntries\"",
        [ProjectStorageTables.AnalysisMetrics] = "VACUUM (FULL, ANALYZE) \"AnalysisMetrics\"",
        [ProjectStorageTables.AnalysisComponents] = "VACUUM (FULL, ANALYZE) \"AnalysisComponents\""
    };

    internal const string SizeStatement = "SELECT pg_total_relation_size(to_regclass(@relation)) AS \"Value\"";

    /// <summary>A rewrite of a multi-gigabyte table takes minutes; the default 30 s would abort it.</summary>
    private static readonly TimeSpan VacuumTimeout = TimeSpan.FromHours(3);

    public async Task<long?> GetTableBytesAsync(string table, CancellationToken ct = default)
    {
        Require(table);
        if (!db.Database.IsRelational()) return null;
        return await db.Database
            .SqlQueryRaw<long?>(SizeStatement, new NpgsqlParameter("relation", $"\"{table}\""))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task VacuumFullAsync(string table, CancellationToken ct = default)
    {
        var statement = Require(table);
        if (!db.Database.IsRelational()) return;
        db.Database.SetCommandTimeout(VacuumTimeout);
        await db.Database.ExecuteSqlRawAsync(statement, ct).ConfigureAwait(false);
    }

    public Task<int> CountActiveRunsAsync(CancellationToken ct = default) =>
        db.PipelineRuns.AsNoTracking().CountAsync(run => !PipelineStatusFacts.Terminal.Contains(run.Status), ct);

    private static string Require(string table) =>
        Statements.TryGetValue(table, out var statement)
            ? statement
            : throw new ArgumentException($"No space can be reclaimed from table '{table}'.", nameof(table));
}
