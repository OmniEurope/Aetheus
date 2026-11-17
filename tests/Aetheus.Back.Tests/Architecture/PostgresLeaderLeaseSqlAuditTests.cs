// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

public class PostgresLeaderLeaseSqlAuditTests
{
    [Fact]
    public void LeaderLease_UsesOnlyAdr029Statements()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Aetheus.Back", "Services", "PostgresLeaderLease.cs"));
        var statements = Regex.Matches(source, "new NpgsqlCommand\\(\\s*\"(?<sql>SELECT [^\"]+)\"", RegexOptions.Multiline)
            .Select(match => match.Groups["sql"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "SELECT pg_advisory_lock(hashtextextended(@name, 0))",
            "SELECT pg_try_advisory_lock(hashtextextended(@name, 0))",
            "SELECT 1",
            "SELECT pg_advisory_unlock(hashtextextended(@name, 0))",
            // Audit E4 (user decision of 2026-11-14): the port claims' lock, held to the caller's transaction end.
            "SELECT pg_advisory_xact_lock(hashtextextended(@name, 0))"
        };

        Assert.Equal(expected.Order(), statements.Order());
        Assert.DoesNotContain("$\"SELECT", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
