// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

public sealed partial class ExpandContractMigrationAuditTests
{
    private const string GuardBaseline = "20260719103123_AddFirewallCollectionDiagnostics.cs";
    private const string SqlReviewMarker = "AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED:";

    /// <summary>
    /// Contract migrations allowed to drop a column or a table, through reviewed raw SQL, because the
    /// previous release (V-1) already stopped mapping what they drop. Each entry is a user-approved
    /// contract step listed in docs/contracts/deployment.md (pending contract steps); any other
    /// migration whose Up() drops a column or a table fails this guard.
    /// </summary>
    private static readonly HashSet<string> ReviewedContractDrops = new(StringComparer.Ordinal)
    {
        // Recette R2-001: Environments.AdvanceBranchOnDeploy / AdvanceBranchName, unmapped by
        // 20261002163217_UnmapRetiredEnvironmentBranchAdvance (shipped in v1.3.6 and v1.3.9).
        "20261010093813_DropRetiredEnvironmentBranchAdvanceColumns.cs",
    };

    [Fact]
    public void FutureMigrationUpMethods_RespectExpandContract()
    {
        var migrations = RepositoryScan.EnumerateTopLevel(Path.Combine(FindRepoRoot(), "src", "Aetheus.Back", "Data", "Migrations"), "*.cs")
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.Ordinal)
                           && !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)
                           && string.CompareOrdinal(Path.GetFileName(path), GuardBaseline) > 0)
            .ToList();
        var violations = new List<string>();

        foreach (var path in migrations)
        {
            var source = File.ReadAllText(path);
            var up = ExtractUpMethod(source);
            if (DestructiveOperationRegex().IsMatch(up))
                violations.Add($"{Path.GetFileName(path)} uses a destructive EF operation in Up().");
            var sqlMatch = SqlCallRegex().Match(up);
            if (sqlMatch.Success)
            {
                var markerIndex = up.IndexOf(SqlReviewMarker, StringComparison.Ordinal);
                if (markerIndex < 0 || markerIndex >= sqlMatch.Index)
                    violations.Add($"{Path.GetFileName(path)} uses migrationBuilder.Sql without a preceding reviewed rationale.");
            }
            if (SqlDropRegex().IsMatch(up) && !ReviewedContractDrops.Contains(Path.GetFileName(path)))
                violations.Add($"{Path.GetFileName(path)} drops a column or a table in Up() without being a named reviewed contract drop.");
        }

        Assert.True(violations.Count == 0,
            "Future migrations must be expand-only until V+2. " + string.Join(" ", violations));
    }

    [Fact]
    public void ReviewedContractDrops_NameExistingMigrations()
    {
        var directory = Path.Combine(FindRepoRoot(), "src", "Aetheus.Back", "Data", "Migrations");
        var missing = ReviewedContractDrops.Where(name => !File.Exists(Path.Combine(directory, name))).ToList();

        Assert.True(missing.Count == 0, "Reviewed contract drops name missing migrations: " + string.Join(", ", missing));
    }

    private static string ExtractUpMethod(string source)
    {
        var start = source.IndexOf("protected override void Up(MigrationBuilder migrationBuilder)", StringComparison.Ordinal);
        var end = source.IndexOf("protected override void Down(MigrationBuilder migrationBuilder)", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Migration Up()/Down() methods could not be located.");
        return source[start..end];
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;

    [GeneratedRegex(@"migrationBuilder\.(?:Drop(?:Column|Table|ForeignKey|PrimaryKey|UniqueConstraint|CheckConstraint|Index|Sequence)|Rename(?:Column|Table|Index|Sequence)|AlterColumn)(?:<[^>]+>)?\s*\(")]
    private static partial Regex DestructiveOperationRegex();

    [GeneratedRegex(@"migrationBuilder\s*\.\s*Sql\s*\(")]
    private static partial Regex SqlCallRegex();

    [GeneratedRegex(@"DROP\s+(?:COLUMN|TABLE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlDropRegex();
}
