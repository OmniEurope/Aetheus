// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.SystemLogs;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>The table-volume SQL exception (R-6) is one fixed, parameterless statement that reads the
/// PostgreSQL catalog only, never a row of an application table, reachable only from the administrator
/// endpoint, as documented in AGENTS.md.</summary>
public sealed class DatabaseTableVolumeSqlAuditTests
{
    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back", "Components", "SystemLogs", file));

    [Fact]
    public void TheStatement_IsFixedReadOnlyCatalogText()
    {
        var statement = DatabaseTableVolumeRepository.Statement;

        Assert.StartsWith("SELECT ", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("@", statement, StringComparison.Ordinal);
        Assert.DoesNotContain(";", statement, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)\b(insert|update|delete|drop|alter|truncate|vacuum|count\s*\(|max\s*\()", statement);
        // Every relation it reads is a pg_catalog one.
        var relations = Regex.Matches(statement, @"(?i)\b(?:FROM|JOIN)\s+(?!LATERAL)(?<name>[a-z_]+)")
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(relations, name => Assert.StartsWith("pg_", name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheRepository_BuildsNoSqlAtRuntime()
    {
        var source = Source("DatabaseTableVolumeRepository.cs");

        Assert.Single(Regex.Matches(source, "SqlQueryRaw"));
        Assert.DoesNotContain("$\"SELECT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteSqlRaw", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheAdministratorEndpoint_ReadsTheTableVolumes()
    {
        var callers = RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back"), "*.cs")
            .Where(file => File.ReadAllText(file).Contains("IDatabaseTableVolumeService", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["DatabaseTableVolumeController.cs", "DatabaseTableVolumeService.cs", "SystemLogsModuleExtensions.cs"],
            callers);
        Assert.Contains("[Authorize(Roles = \"Admin\")]", Source("DatabaseTableVolumeController.cs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("smallint", short.MaxValue)]
    [InlineData("integer", int.MaxValue)]
    [InlineData("bigint", long.MaxValue)]
    public void TheLimit_IsTheLargestValueOfTheKeyType(string keyType, long expected) =>
        Assert.Equal(expected, DatabaseTableVolumeService.LimitOf(keyType));

    [Fact]
    public async Task TheView_ListsTheTableClosestToItsLimitFirst_ThenByRows()
    {
        var service = new DatabaseTableVolumeService(new FixedRepository(
        [
            new() { TableName = "Small", EstimatedRows = 5, TotalBytes = 10 },
            new() { TableName = "Big", EstimatedRows = 900, TotalBytes = 10 },
            new() { TableName = "Half", EstimatedRows = 1, TotalBytes = 10, KeyType = "integer", LastKey = int.MaxValue / 2 },
            new() { TableName = "Tiny", EstimatedRows = 1, TotalBytes = 10, KeyType = "integer", LastKey = 10 }
        ]));

        var tables = await service.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Half", "Tiny", "Big", "Small"], tables.Select(table => table.Name).ToArray());
        Assert.InRange(tables[0].KeyLimitPercent!.Value, 49.9, 50.1);
        Assert.Null(tables[2].KeyLimitPercent);
    }

    private sealed class FixedRepository(IReadOnlyList<DatabaseTableVolumeRow> rows) : IDatabaseTableVolumeRepository
    {
        public Task<IReadOnlyList<DatabaseTableVolumeRow>> ListAsync(CancellationToken ct = default) => Task.FromResult(rows);
    }
}
