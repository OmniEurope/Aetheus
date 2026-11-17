// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The expand/contract gate: refuses a pending migration whose <c>Up()</c> would break the colour
/// that is still serving.
///
/// Blue-green shares one database between both colours. While the new colour migrates, the old one
/// is still answering requests against the same schema, so a migration that drops or renames
/// anything takes production down mid-deployment. Expand and contract therefore have to ship in
/// separate releases, and that is exactly what this refuses to let through.
///
/// This is also why the deployment takes no database backup: a backup would let a violated contract
/// through and leave the damage to be undone afterwards, instead of stopping it before it runs.
/// </summary>
internal static partial class BlueGreenMigrationGate
{
    /// <summary>
    /// Operations that are safe to add but never safe to apply while another replica serves the same
    /// schema. <c>AlterColumn</c> is included because widening and narrowing are indistinguishable
    /// here, and narrowing breaks the live colour.
    /// </summary>
    [GeneratedRegex(
        @"migrationBuilder\.(Drop(Column|Table|ForeignKey|PrimaryKey|UniqueConstraint|CheckConstraint|Index|Sequence)|Rename(Column|Table|Index|Sequence)|AlterColumn)(<[^>]+>)?\s*\(",
        RegexOptions.Compiled)]
    private static partial Regex DestructiveOperationRegex();

    [GeneratedRegex(@"protected\s+override\s+void\s+Up\s*\(\s*MigrationBuilder\s+\w+\s*\)", RegexOptions.Compiled)]
    private static partial Regex UpMethodRegex();

    [GeneratedRegex(@"protected\s+override\s+void\s+Down\s*\(\s*MigrationBuilder\s+\w+\s*\)", RegexOptions.Compiled)]
    private static partial Regex DownMethodRegex();

    [GeneratedRegex(@"migrationBuilder\s*\.\s*Sql\s*\(", RegexOptions.Compiled)]
    private static partial Regex RawSqlRegex();

    /// <summary>
    /// Adding a column is the archetypal expand operation - except when it is <c>NOT NULL</c> with no
    /// default. Postgres then rejects the statement on any non-empty table, and even on an empty one
    /// the colour still serving inserts rows without that column and starts failing. So the shape is
    /// checked, not just the verb.
    /// </summary>
    [GeneratedRegex(@"migrationBuilder\s*\.\s*AddColumn(<[^>]+>)?\s*\(", RegexOptions.Compiled)]
    private static partial Regex AddColumnRegex();

    [GeneratedRegex(@"nullable\s*:\s*false", RegexOptions.Compiled)]
    private static partial Regex NonNullableArgumentRegex();

    [GeneratedRegex(@"defaultValue(Sql)?\s*:", RegexOptions.Compiled)]
    private static partial Regex DefaultValueArgumentRegex();

    /// <summary>Marker a migration author writes above raw SQL to record that they checked it is expand-safe.</summary>
    internal const string SqlReviewMarker = "AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED:";

    /// <summary>
    /// Inspects one migration source. Returns every violation found; an empty list means the
    /// migration is safe to apply while the previous colour keeps serving.
    /// </summary>
    internal static IReadOnlyList<string> Inspect(string migrationId, string source)
    {
        var violations = new List<string>();
        var lines = source.Split('\n');

        // Raw SQL is opaque to this analysis, so it is allowed only with an explicit rationale
        // written above it. An unreviewed Sql(...) call is treated as a violation rather than
        // trusted, because the whole point is that nobody can see what it does from here.
        // EVERY raw SQL call is inspected, not just the first. Checking only the first meant a migration
        // whose first Sql(...) carried a rationale could follow it with any number of unreviewed ones,
        // which is precisely the shape this gate exists to stop. Each call must have its own marker
        // above it and below the previous call, so one rationale cannot cover a later, unrelated statement.
        var previousSqlLine = -1;
        for (var sqlLine = IndexOfMatchFrom(lines, RawSqlRegex(), 0);
             sqlLine >= 0;
             sqlLine = IndexOfMatchFrom(lines, RawSqlRegex(), sqlLine + 1))
        {
            var reviewLine = Array.FindLastIndex(
                lines, sqlLine, line => line.Contains(SqlReviewMarker, StringComparison.Ordinal));
            if (reviewLine < 0 || reviewLine <= previousSqlLine)
            {
                violations.Add(
                    $"{migrationId}: migrationBuilder.Sql(...) at line {sqlLine + 1} requires a preceding "
                    + $"{SqlReviewMarker} rationale.");
            }
            previousSqlLine = sqlLine;
        }

        var sawUp = false;
        var insideUp = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (UpMethodRegex().IsMatch(line))
            {
                insideUp = true;
                sawUp = true;
                // No `continue`: an expression-bodied `Up(MigrationBuilder mb) => mb.DropColumn(...)`
                // carries the operation on the signature line itself, and skipping it let exactly the
                // migrations this gate exists to stop pass through unexamined.
            }
            else if (DownMethodRegex().IsMatch(line))
            {
                insideUp = false;
                continue;
            }
            // Down() may legitimately drop what Up() added, so only Up() is inspected.
            if (!insideUp) continue;
            if (DestructiveOperationRegex().IsMatch(line))
                violations.Add($"{migrationId}: line {index + 1} is not expand-compatible: {line.Trim()}");
            if (AddColumnRegex().IsMatch(line))
                InspectAddColumn(migrationId, lines, index, violations);
        }

        if (!sawUp)
            violations.Add($"{migrationId}: no Up(MigrationBuilder) method could be inspected.");

        return violations;
    }

    /// <summary>
    /// Reads one <c>AddColumn</c> call from its opening line to the end of the statement, because EF
    /// generates it across several lines and <c>nullable:</c> is rarely on the same one as the verb.
    /// </summary>
    private static void InspectAddColumn(string migrationId, string[] lines, int start, List<string> violations)
    {
        var statement = new System.Text.StringBuilder();
        for (var index = start; index < lines.Length && index < start + MaxStatementLines; index++)
        {
            statement.Append(lines[index]);
            if (lines[index].Contains(';', StringComparison.Ordinal)) break;
        }
        var text = statement.ToString();
        if (!NonNullableArgumentRegex().IsMatch(text) || DefaultValueArgumentRegex().IsMatch(text)) return;
        violations.Add(
            $"{migrationId}: line {start + 1} adds a NOT NULL column with no defaultValue, which the colour "
            + $"still serving cannot satisfy: {lines[start].Trim()}");
    }

    /// <summary>Ceiling on how far one statement is followed, so a malformed source cannot walk the file.</summary>
    private const int MaxStatementLines = 40;

    private static int IndexOfMatchFrom(string[] lines, Regex regex, int startIndex)
    {
        for (var index = startIndex; index < lines.Length; index++)
            if (regex.IsMatch(lines[index])) return index;
        return -1;
    }

    /// <summary>
    /// The migration ids present in the source tree, in EF's ordering. Designer files are the
    /// generated model snapshot companions, never migrations in their own right.
    /// </summary>
    internal static IReadOnlyList<string> DiscoverMigrationIds(string migrationsDirectory)
    {
        if (!Directory.Exists(migrationsDirectory)) return [];
        return Directory.EnumerateFiles(migrationsDirectory, "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name)
                && !name!.EndsWith(".Designer", StringComparison.Ordinal)
                && char.IsDigit(name[0]))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Migrations present in the tree that the database has not recorded as applied.</summary>
    internal static IReadOnlyList<string> Pending(
        IReadOnlyList<string> discovered, IReadOnlyCollection<string> applied)
    {
        var appliedSet = new HashSet<string>(applied, StringComparer.Ordinal);
        return discovered.Where(id => !appliedSet.Contains(id)).ToList();
    }
}
