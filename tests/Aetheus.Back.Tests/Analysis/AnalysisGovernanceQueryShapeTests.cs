// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// R-44: reading a finding to decide on it logged two Entity Framework warnings in production, a limit without an order
/// (the one occurrence of the CVE watch) and two collections in one query. The two warnings are thrown here, as the
/// Development hosts throw them, and the query is translated against a server that is never reached.
/// </summary>
public sealed class AnalysisGovernanceQueryShapeTests
{
    private static AppDbContext UnreachablePostgres() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=never;Timeout=1;Command Timeout=1")
        .ConfigureWarnings(warnings => warnings.Throw(
            CoreEventId.RowLimitingOperationWithoutOrderByWarning,
            CoreEventId.FirstWithoutOrderByAndFilterWarning,
            RelationalEventId.MultipleCollectionIncludeWarning))
        .Options);

    [Fact]
    public async Task ReadingATrackedFinding_RaisesNoQueryShapeWarning()
    {
        await using var db = UnreachablePostgres();
        var repository = new AnalysisGovernanceRepository(db);

        // The query is translated before any connection is opened: a warning is an InvalidOperationException naming
        // it, anything else (the server being unreachable) means the query shape is accepted.
        var failure = await Record.ExceptionAsync(() => repository.GetTrackedFindingAsync(1, TestContext.Current.CancellationToken));

        Assert.NotNull(failure);
        Assert.DoesNotContain("WithoutOrderBy", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("MultipleCollectionInclude", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadingATrackedFinding_KeepsTheFirstCveWatchOccurrence_AndItsDecisions()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var finding = new AnalysisFinding();
        db.AnalysisFindings.Add(finding);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.AnalysisFindingOccurrences.AddRange(
            Occurrence(finding.Id, "sonar"), Occurrence(finding.Id, AcceptedRiskDuration.CveWatchScannerKey), Occurrence(finding.Id, AcceptedRiskDuration.CveWatchScannerKey));
        db.AnalysisFindingDecisions.Add(new AnalysisFindingDecision { AnalysisFindingId = finding.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var read = await new AnalysisGovernanceRepository(db).GetTrackedFindingAsync(finding.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        var occurrence = Assert.Single(read.Occurrences);
        Assert.Equal(AcceptedRiskDuration.CveWatchScannerKey, occurrence.ScannerKey);
        Assert.Equal(db.AnalysisFindingOccurrences.Where(item => item.ScannerKey == AcceptedRiskDuration.CveWatchScannerKey).Min(item => item.Id), occurrence.Id);
        Assert.Single(read.Decisions);
    }

    private static AnalysisFindingOccurrence Occurrence(int findingId, string scannerKey) =>
        new() { AnalysisFindingId = findingId, ScannerKey = scannerKey };
}
