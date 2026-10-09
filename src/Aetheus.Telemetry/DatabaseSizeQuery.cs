// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;

namespace Aetheus.Telemetry;

/// <summary>The size of the database and of its largest tables at one instant.</summary>
public sealed record DatabaseSizeSnapshot(string DatabaseName, long DatabaseBytes, IReadOnlyList<DatabaseTableSize> Tables);

/// <summary>One table: its total size on disk (heap, indexes and TOAST, what <c>pg_total_relation_size</c>
/// counts) and the planner's row estimate (<c>pg_class.reltuples</c>), null while PostgreSQL has never
/// analysed the table.</summary>
public sealed record DatabaseTableSize(string Schema, string Name, long TotalBytes, long? EstimatedRows);

/// <summary>
/// The two catalog statements of the measurement. Fixed text, one integer parameter: they read sizes and
/// statistics from <c>pg_catalog</c> and never select a row of an application table.
/// </summary>
internal static class DatabaseSizeQuery
{
    internal const string DatabaseStatement =
        "SELECT current_database(), pg_database_size(current_database())";

    /// <summary>Ordinary tables and materialised views of every user schema, the largest first. A
    /// partitioned parent holds no data of its own (its partitions are listed), TOAST and temporary
    /// schemas belong to the tables that own them.</summary>
    internal const string TablesStatement =
        "SELECT n.nspname, c.relname, pg_total_relation_size(c.oid), c.reltuples::float8 "
        + "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
        + "WHERE c.relkind IN ('r', 'm') "
        + "AND n.nspname NOT IN ('pg_catalog', 'information_schema') "
        + "AND n.nspname NOT LIKE 'pg\\_toast%' AND n.nspname NOT LIKE 'pg\\_temp%' "
        + "ORDER BY pg_total_relation_size(c.oid) DESC, n.nspname, c.relname "
        + "LIMIT @top";

    internal const string TopParameter = "top";
    private const int CommandTimeoutSeconds = 30;

    internal static async Task<DatabaseSizeSnapshot> ReadAsync(DbConnection connection, int topTables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string databaseName;
        long databaseBytes;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = DatabaseStatement;
            command.CommandTimeout = CommandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("The database size query returned no row.");
            databaseName = reader.GetString(0);
            databaseBytes = reader.GetInt64(1);
        }

        var tables = new List<DatabaseTableSize>(topTables);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = TablesStatement;
            command.CommandTimeout = CommandTimeoutSeconds;
            var top = command.CreateParameter();
            top.ParameterName = TopParameter;
            top.Value = topTables;
            command.Parameters.Add(top);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                tables.Add(MapTable(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetDouble(3)));
        }
        return new DatabaseSizeSnapshot(databaseName, databaseBytes, tables);
    }

    /// <summary>PostgreSQL 14 and later write -1 in <c>reltuples</c> for a table never vacuumed nor
    /// analysed: unknown, not zero.</summary>
    internal static DatabaseTableSize MapTable(string schema, string name, long totalBytes, double reltuples) =>
        new(schema, name, Math.Max(0, totalBytes),
            reltuples < 0 || !double.IsFinite(reltuples) ? null : (long)Math.Round(reltuples));
}
