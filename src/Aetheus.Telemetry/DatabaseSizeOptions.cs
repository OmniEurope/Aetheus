// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;

namespace Aetheus.Telemetry;

/// <summary>Optionally measure database and largest-table sizes from PostgreSQL metadata without
/// reading table contents. Enable through a host-provided connection, with configuration bounding
/// interval and table count.</summary>
public sealed class DatabaseSizeOptions
{
    public const int DefaultIntervalMinutes = 15;
    public const int DefaultTopTables = 20;

    public bool Enabled { get; set; }

    /// <summary>Minutes between two measurements: 5 to 1440 (one day).</summary>
    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    /// <summary>How many tables are measured, the largest first: 1 to 100.</summary>
    public int TopTables { get; set; } = DefaultTopTables;

    /// <summary>Opens a new, unopened connection to the PostgreSQL database to measure. The host's own
    /// connection string: the measurement reads only <c>pg_catalog</c> functions and statistics.</summary>
    public Func<DbConnection>? ConnectionFactory { get; set; }

    /// <summary>Turns the measurement on for a PostgreSQL database reached through
    /// <paramref name="connectionFactory"/>. The <c>AETHEUS_TELEMETRY_DATABASE_SIZE_ENABLED</c> environment
    /// variable still has the final say.</summary>
    public DatabaseSizeOptions UsePostgreSql(Func<DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ConnectionFactory = connectionFactory;
        Enabled = true;
        return this;
    }

    internal void Validate()
    {
        if (!Enabled)
            return;
        if (ConnectionFactory is null)
            throw new InvalidOperationException(
                "The Aetheus database size measurement requires a connection factory (DatabaseSize.UsePostgreSql).");
        if (IntervalMinutes is < 5 or > 1440)
            throw new InvalidOperationException("DatabaseSize.IntervalMinutes must be between 5 and 1440.");
        if (TopTables is < 1 or > 100)
            throw new InvalidOperationException("DatabaseSize.TopTables must be between 1 and 100.");
    }
}
