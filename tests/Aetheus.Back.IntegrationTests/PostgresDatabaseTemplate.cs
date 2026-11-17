// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The migrated template every integration database is cloned from. Test classes run in parallel, each on
/// its own database, so a drop only ever clears the pool of the database it drops: clearing every pool of
/// the process would close the idle connections of every class running beside it, and their reconnections
/// exhaust the client's ephemeral ports.
/// </summary>
internal static class PostgresDatabaseTemplate
{
    private const string TemplateDatabase = "aetheus_it_template";

    public static async Task InitializeAsync(string connectionString)
    {
        var templateConnectionString = WithDatabase(connectionString, TemplateDatabase);
        await ExecuteAdminAsync(connectionString,
            $"DROP DATABASE IF EXISTS {TemplateDatabase} WITH (FORCE); CREATE DATABASE {TemplateDatabase};");

        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(templateConnectionString)
            .Options))
        {
            await db.Database.MigrateAsync();
        }

        // No session may stay on the template: PostgreSQL refuses to copy a database someone is connected to.
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>Creates the database of <paramref name="connectionString"/> as a copy of the template.</summary>
    public static async Task CreateAsync(string connectionString) =>
        await ExecuteAdminAsync(connectionString, $"CREATE DATABASE {SafeDatabase(connectionString)} TEMPLATE {TemplateDatabase};");

    /// <summary>Drops the database of <paramref name="connectionString"/>, closing its sessions.</summary>
    public static async Task DropAsync(string connectionString)
    {
        var database = SafeDatabase(connectionString);
        ClearPool(connectionString);
        await ExecuteAdminAsync(connectionString, $"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
    }

    /// <summary>Drops the database of <paramref name="connectionString"/> and copies the template again under
    /// the same name. Only the pool of that exact connection string is cleared.</summary>
    // CA2100 sees an interpolated database name in DDL. PostgreSQL does not accept a parameter for
    // an identifier, so this cannot be bound; SafeDatabase is the guard instead, and it refuses
    // anything outside [A-Za-z0-9_] before the name reaches the statement.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "DDL identifier cannot be a parameter; SafeDatabase restricts it to [A-Za-z0-9_].")]
    public static void Reset(string connectionString)
    {
        var database = SafeDatabase(connectionString);
        ClearPool(connectionString);
        using var connection = new NpgsqlConnection(WithDatabase(connectionString, "postgres"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP DATABASE IF EXISTS {database} WITH (FORCE); CREATE DATABASE {database} TEMPLATE {TemplateDatabase};";
        command.ExecuteNonQuery();
    }

    private static void ClearPool(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test-only admin helper; every caller in this file passes a literal statement or a SafeDatabase name.")]
    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(WithDatabase(connectionString, "postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    private static string SafeDatabase(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrWhiteSpace(database) || database.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidOperationException($"Unsafe integration database name: '{database}'.");
        return database;
    }
}
