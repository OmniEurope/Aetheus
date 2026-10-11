// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Runs the deploy-time expand/contract gate over the repository's own migrations, so a migration it
/// would refuse fails here instead of in aetheus-deploy-prod. Run 2679 (2026-10-10) reached the
/// "Reconcile the shared schema" step after a two-hour candidate, only to be refused on an unreviewed
/// Sql(...) call in Down(): nothing ran the gate on the sources before then.
/// </summary>
public sealed class BlueGreenMigrationGateRepositoryTests
{
    /// <summary>
    /// The last migration that predates this guard and that the gate would refuse. Every migration up to
    /// it is already applied in production, and the gate only inspects pending ones, so it never sees them
    /// again. Every later migration must pass. Never move this cutoff forward to admit a new migration.
    /// </summary>
    private const string LastMigrationAppliedBeforeThisGuard = "20260912184518_AnalyticsVaultPerProject";

    [Fact]
    public void EveryMigrationAfterTheCutoff_PassesTheBlueGreenGate()
    {
        var directory = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back", "Data", "Migrations");
        var migrationIds = BlueGreenMigrationGate.DiscoverMigrationIds(directory);
        Assert.Contains(LastMigrationAppliedBeforeThisGuard, migrationIds);

        var inspected = migrationIds
            .Where(id => string.CompareOrdinal(id, LastMigrationAppliedBeforeThisGuard) > 0)
            .ToList();
        Assert.NotEmpty(inspected);

        var violations = inspected
            .SelectMany(id => BlueGreenMigrationGate.Inspect(id, File.ReadAllText(Path.Combine(directory, id + ".cs"))))
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
