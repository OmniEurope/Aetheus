// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>
/// One fixed, parameterless catalog statement under the AGENTS.md raw-SQL exception pinned by
/// <c>DatabaseTableVolumeSqlAuditTests</c>: it reads <c>pg_class</c>, <c>pg_index</c>, <c>pg_attribute</c>
/// and the sequence of the key, never a row of an application table (no <c>count(*)</c>, no <c>max</c>).
/// </summary>
public sealed class DatabaseTableVolumeRepository(AppDbContext db) : IDatabaseTableVolumeRepository
{
    /// <summary>Ordinary tables of the non-system schemas. The key columns are filled only for a
    /// single-column primary key of type smallint, integer or bigint; the sequence last value is null
    /// when no sequence feeds it or it was never used.</summary>
    internal const string Statement =
        "SELECT c.relname AS \"TableName\", "
        + "CASE WHEN c.reltuples >= 0 THEN c.reltuples::float8 ELSE st.n_live_tup::float8 END AS \"EstimatedRows\", "
        + "pg_total_relation_size(c.oid) AS \"TotalBytes\", "
        + "CASE WHEN pk.atttypid IN (21, 23, 20) THEN format_type(pk.atttypid, NULL) END AS \"KeyType\", "
        + "CASE WHEN pk.atttypid IN (21, 23, 20) THEN pg_sequence_last_value(pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), pk.attname)::regclass) END AS \"LastKey\" "
        + "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
        + "LEFT JOIN pg_stat_user_tables st ON st.relid = c.oid "
        + "LEFT JOIN LATERAL (SELECT a.attname, a.atttypid FROM pg_index i "
        + "JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[0] "
        + "WHERE i.indrelid = c.oid AND i.indisprimary AND i.indnatts = 1) pk ON true "
        + "WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog', 'information_schema') "
        + "AND left(n.nspname, 8) <> 'pg_toast' AND left(n.nspname, 7) <> 'pg_temp'";

    public async Task<IReadOnlyList<DatabaseTableVolumeRow>> ListAsync(CancellationToken ct = default)
    {
        if (!db.Database.IsRelational()) return [];
        return await db.Database.SqlQueryRaw<DatabaseTableVolumeRow>(Statement)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
