// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Requires a review rationale above every migrationBuilder.Sql call because raw SQL is
/// opaque to expand/contract analysis.</summary>
public sealed class BlueGreenMigrationGateSqlReviewTests
{
    private const string Marker = "AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED:";

    private static string Migration(string body) =>
        "public partial class M : Migration\n"
        + "{\n"
        + "    protected override void Up(MigrationBuilder migrationBuilder)\n"
        + "    {\n"
        + body
        + "    }\n"
        + "    protected override void Down(MigrationBuilder migrationBuilder) { }\n"
        + "}\n";

    [Fact]
    public void ASecondUnreviewedSqlCall_IsRefused_EvenWhenTheFirstOneIsReviewed()
    {
        var source = Migration(
            $"        // {Marker} backfills the new column, additive only.\n"
            + "        migrationBuilder.Sql(\"UPDATE users SET tier = 'free' WHERE tier IS NULL\");\n"
            + "        migrationBuilder.Sql(\"DELETE FROM sessions\");\n");

        var violations = BlueGreenMigrationGate.Inspect("20260101000000_TwoSql", source);

        var violation = Assert.Single(violations);
        Assert.Contains("migrationBuilder.Sql", violation, StringComparison.Ordinal);
        // The reported line must be the SECOND call, not the reviewed first one.
        Assert.Contains("line 7", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySqlCallWithItsOwnRationale_IsAccepted()
    {
        var source = Migration(
            $"        // {Marker} backfills the new column, additive only.\n"
            + "        migrationBuilder.Sql(\"UPDATE users SET tier = 'free' WHERE tier IS NULL\");\n"
            + $"        // {Marker} seeds the new lookup table, additive only.\n"
            + "        migrationBuilder.Sql(\"INSERT INTO tiers (name) VALUES ('free')\");\n");

        Assert.Empty(BlueGreenMigrationGate.Inspect("20260101000000_TwoReviewed", source));
    }

    [Fact]
    public void OneRationale_CannotCoverTwoCalls()
    {
        // The marker must sit between the previous call and this one, so a single rationale written
        // once at the top cannot silently authorise everything that follows.
        var source = Migration(
            $"        // {Marker} covers everything below, or so this migration hopes.\n"
            + "        migrationBuilder.Sql(\"UPDATE a SET b = 1\");\n"
            + "        migrationBuilder.Sql(\"UPDATE c SET d = 2\");\n"
            + "        migrationBuilder.Sql(\"UPDATE e SET f = 3\");\n");

        var violations = BlueGreenMigrationGate.Inspect("20260101000000_ThreeSql", source);

        Assert.Equal(2, violations.Count);
    }

    [Fact]
    public void ASingleUnreviewedSqlCall_IsStillRefused()
    {
        var source = Migration("        migrationBuilder.Sql(\"DELETE FROM sessions\");\n");

        Assert.Single(BlueGreenMigrationGate.Inspect("20260101000000_OneSql", source));
    }

    [Fact]
    public void ASingleReviewedSqlCall_IsStillAccepted()
    {
        var source = Migration(
            $"        // {Marker} additive backfill.\n"
            + "        migrationBuilder.Sql(\"UPDATE users SET tier = 'free'\");\n");

        Assert.Empty(BlueGreenMigrationGate.Inspect("20260101000000_OneReviewed", source));
    }
}
