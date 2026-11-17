// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The findings of the vulnerability watch on PostgreSQL (PLAN-004, lot 3, points 7 to 9): a match becomes a
/// finding through a report without a run, only when the set changes; a package past the fix or a withdrawn
/// advisory closes it, a regression reopens the same finding. Everything goes through the real matching
/// service, the real publisher and the existing closure, with the clock moved by hand.
/// </summary>
public sealed class CveWatchPublicationIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const string Cve = "CVE-2099-0001";
    private const string UpToFix = """[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"2.0.0"}]}]""";
    private const string Unreadable = """[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"not a version"}]}]""";

    private static readonly OsvRecordParser Parser = new(new HashSet<string>(["NuGet"], StringComparer.Ordinal));

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
    private int _stamp;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A commit that fails once armed: the publication's transaction must then leave nothing behind.</summary>
    private sealed class FailingCommitScope(IDbTransactionScope inner) : IDbTransactionScope
    {
        public bool FailCommit { get; set; }
        public bool IsRelational => inner.IsRelational;
        public bool InTransaction => inner.InTransaction;
        public Task BeginTransactionAsync(CancellationToken ct = default) => inner.BeginTransactionAsync(ct);
        public Task RollbackAsync(CancellationToken ct = default) => inner.RollbackAsync(ct);

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (FailCommit)
                throw new InvalidOperationException("The commit failed on purpose.");
            await inner.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>One whole pass: matching of the changed advisories and inventories, closure, then publication.</summary>
    private async Task PassAsync(AnalysisPlatformOptions? options = null, bool failCommit = false)
    {
        await using var db = NewContext();
        var watch = new CveWatchReportRepository(db);
        var scope = new FailingCommitScope(new DbTransactionScope(db)) { FailCommit = failCommit };
        var publisher = new CveWatchReportPublisher(new AnalysisRepository(db), watch, scope, _clock, Options.Create(options ?? new AnalysisPlatformOptions()));
        var handler = new CveWatchPublication(watch, publisher, NullLogger<CveWatchPublication>.Instance);
        // The real dispatcher, as in production: it resolves the handlers that Analysis registers for the event.
        var handlers = new ServiceCollection()
            .AddSingleton<IDomainEventHandler<VulnerabilityMatchesChangedEvent>>(handler)
            .BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(handlers, Substitute.For<IBackgroundTaskQueue>(), NullLogger<DomainEventDispatcher>.Instance);
        var service = new VulnerabilityMatchService(
            new VulnerabilityMatchRepository(db), _clock, NullLogger<VulnerabilityMatchService>.Instance, dispatcher);
        await service.MatchChangedAdvisoriesAsync(Ct);
        await service.MatchChangedInventoriesAsync(null, Ct);
    }

    private async Task ApplyAsync(params string[] documents)
    {
        await using var db = NewContext();
        var repository = new VulnerabilityFeedRepository(db);
        var state = await repository.GetOrCreateStateAsync("osv:test", Ct);
        var records = new List<OsvAdvisoryRecord>();
        foreach (var document in documents)
            records.Add((await Parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(document)), Ct))!);
        await repository.ApplyBatchAsync(records, state, Ct);
    }

    private static string Advisory(string id, string package, string ranges, int revision = 0, bool withdrawn = false) =>
        "{\"id\":\"" + id + "\",\"aliases\":[\"" + Cve + "\"],\"modified\":\"2026-10-01T10:00:" + (10 + revision).ToString("00") + "Z\""
        + (withdrawn ? ",\"withdrawn\":\"2026-10-01T11:00:00Z\"" : string.Empty)
        + ",\"database_specific\":{\"severity\":\"HIGH\"},\"affected\":[{\"package\":{\"name\":\"" + package
        + "\",\"ecosystem\":\"NuGet\"},\"ranges\":" + ranges + "}]}";

    private async Task<(int OrganizationId, int ProjectId, int InventoryId)> SeedProjectAsync(string name)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = name, Slug = name.ToLowerInvariant(), Description = "d" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = name, Description = "d", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        var inventory = new ProjectInventory
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            Origin = ProjectInventoryOrigin.Sbom,
            ReadAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            ChangedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc)
        };
        db.ProjectInventories.Add(inventory);
        await db.SaveChangesAsync(Ct);
        return (organization.Id, project.Id, inventory.Id);
    }

    /// <summary>Adds an item the way a refresh does: a new row, and the inventory marked as changed.</summary>
    private async Task<int> AddItemAsync((int OrganizationId, int ProjectId, int InventoryId) owner, string package, string version)
    {
        await using var db = NewContext();
        var item = new ProjectInventoryItem
        {
            OrganizationId = owner.OrganizationId,
            ProjectId = owner.ProjectId,
            ProjectInventoryId = owner.InventoryId,
            Ecosystem = "NuGet",
            PackageName = package,
            Version = version,
            PackageUrl = $"pkg:nuget/{package}@{version}"
        };
        db.ProjectInventoryItems.Add(item);
        (await db.ProjectInventories.SingleAsync(row => row.Id == owner.InventoryId, Ct)).ChangedAt =
            new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc).AddMinutes(++_stamp);
        await db.SaveChangesAsync(Ct);
        return item.Id;
    }

    /// <summary>A version bump as a refresh stores it: the old item goes, a new one comes.</summary>
    private async Task<int> MoveItemAsync((int OrganizationId, int ProjectId, int InventoryId) owner, int oldItem, string package, string version)
    {
        await using (var db = NewContext())
        {
            db.ProjectInventoryItems.Remove(await db.ProjectInventoryItems.SingleAsync(row => row.Id == oldItem, Ct));
            await db.SaveChangesAsync(Ct);
        }

        return await AddItemAsync(owner, package, version);
    }

    private async Task<List<AnalysisReport>> ReportsAsync(int projectId)
    {
        await using var db = NewContext();
        return await db.AnalysisReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId && report.ScannerKey == "cve-watch")
            .Include(report => report.Occurrences).OrderBy(report => report.Id).ToListAsync(Ct);
    }

    private async Task<List<AnalysisFinding>> FindingsAsync(int projectId)
    {
        await using var db = NewContext();
        return await db.AnalysisFindings.AsNoTracking().Where(finding => finding.ProjectId == projectId).OrderBy(finding => finding.Id).ToListAsync(Ct);
    }

    private async Task<List<VulnerabilityMatch>> MatchesAsync(int projectId)
    {
        await using var db = NewContext();
        return await db.VulnerabilityMatches.AsNoTracking().Where(match => match.ProjectId == projectId).OrderBy(match => match.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task AMatch_BecomesAFinding_ThroughAReportWithoutARun()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var item = await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));

        await PassAsync();

        var finding = Assert.Single(await FindingsAsync(owner.ProjectId));
        Assert.Equal(Cve, finding.RuleId);
        Assert.Equal(CveWatchFindings.FingerprintOf(owner.ProjectId, Cve, "NuGet", "lib"), finding.Fingerprint);
        Assert.Equal(AnalysisCategory.Dependencies, finding.Category);
        Assert.Equal(AnalysisSeverity.High, finding.Severity);
        Assert.Equal(AnalysisConfidence.High, finding.Confidence);
        Assert.Equal(AnalysisFindingStatus.Open, finding.Status);
        Assert.Equal(owner.OrganizationId, finding.OrganizationId);
        var report = Assert.Single(await ReportsAsync(owner.ProjectId));
        Assert.Null(report.PipelineRunId);
        Assert.Equal(AnalysisCategory.Dependencies, report.Category);
        Assert.Equal(AnalysisReportStatus.Passed, report.Status);
        Assert.Equal(owner.OrganizationId, report.OrganizationId);
        var occurrence = Assert.Single(report.Occurrences);
        Assert.Equal(finding.Id, occurrence.AnalysisFindingId);
        Assert.True(occurrence.IsNew);
        var match = Assert.Single(await MatchesAsync(owner.ProjectId));
        Assert.Equal(item, match.ProjectInventoryItemId);
        Assert.Equal(finding.Id, match.AnalysisFindingId);
        await using var db = NewContext();
        Assert.Empty(await db.AnalysisEvaluations.Where(evaluation => evaluation.ProjectId == owner.ProjectId).ToListAsync(Ct));
    }

    [Fact]
    public async Task APassWithNoChange_PublishesNoReport()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));
        await PassAsync();
        Assert.Single(await ReportsAsync(owner.ProjectId));

        await PassAsync();
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix, revision: 1));
        await PassAsync();
        await AddItemAsync(owner, "unrelated", "1.0.0");
        await PassAsync();

        Assert.Single(await ReportsAsync(owner.ProjectId));
        Assert.Single(await FindingsAsync(owner.ProjectId));
    }

    [Fact]
    public async Task APackagePastTheFix_ClosesTheMatchAndItsFinding_AndARegressionReopensTheSameOnes()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var item = await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));
        await PassAsync();
        var finding = Assert.Single(await FindingsAsync(owner.ProjectId));
        var match = Assert.Single(await MatchesAsync(owner.ProjectId));

        item = await MoveItemAsync(owner, item, "lib", "2.0.0");
        await PassAsync();

        var fixedFinding = Assert.Single(await FindingsAsync(owner.ProjectId));
        Assert.Equal(finding.Id, fixedFinding.Id);
        Assert.Equal(AnalysisFindingStatus.Fixed, fixedFinding.Status);
        Assert.NotNull(fixedFinding.ResolvedAt);
        var closed = Assert.Single(await MatchesAsync(owner.ProjectId));
        Assert.Equal(match.Id, closed.Id);
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal(2, (await ReportsAsync(owner.ProjectId)).Count);

        await PassAsync();
        Assert.Equal(2, (await ReportsAsync(owner.ProjectId)).Count);

        await MoveItemAsync(owner, item, "lib", "1.5.0");
        await PassAsync();

        var reopened = Assert.Single(await FindingsAsync(owner.ProjectId));
        Assert.Equal(finding.Id, reopened.Id);
        Assert.Equal(AnalysisFindingStatus.Open, reopened.Status);
        Assert.Null(reopened.ResolvedAt);
        var again = Assert.Single(await MatchesAsync(owner.ProjectId));
        Assert.Equal(match.Id, again.Id);
        Assert.Null(again.ClosedAt);
        Assert.Equal(finding.Id, again.AnalysisFindingId);
        var reports = await ReportsAsync(owner.ProjectId);
        Assert.Equal(3, reports.Count);
        Assert.True(Assert.Single(reports[2].Occurrences).IsNew);
    }

    [Fact]
    public async Task AnInventoryDeletedWithItsItems_ClosesTheMatchesAndTheFindingsTheyPublished()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var other = await SeedProjectAsync("Beta");
        await AddItemAsync(owner, "lib", "1.0.0");
        await AddItemAsync(other, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));
        await PassAsync();
        Assert.Equal(AnalysisFindingStatus.Open, Assert.Single(await FindingsAsync(owner.ProjectId)).Status);
        Assert.Equal(AnalysisFindingStatus.Open, Assert.Single(await FindingsAsync(other.ProjectId)).Status);

        await using (var db = NewContext())
        {
            db.ProjectInventories.Remove(await db.ProjectInventories.SingleAsync(row => row.Id == owner.InventoryId, Ct));
            await db.SaveChangesAsync(Ct);
        }

        await PassAsync();

        var closed = Assert.Single(await FindingsAsync(owner.ProjectId));
        Assert.Equal(AnalysisFindingStatus.Fixed, closed.Status);
        Assert.NotNull(closed.ResolvedAt);
        Assert.NotNull(Assert.Single(await MatchesAsync(owner.ProjectId)).ClosedAt);
        Assert.Equal(AnalysisFindingStatus.Open, Assert.Single(await FindingsAsync(other.ProjectId)).Status);
        Assert.Null(Assert.Single(await MatchesAsync(other.ProjectId)).ClosedAt);
    }

    [Fact]
    public async Task ADecisionOnTheFinding_StandsWhenTheCveComesBack()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var item = await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));
        await PassAsync();
        await using (var db = NewContext())
        {
            var finding = await db.AnalysisFindings.SingleAsync(row => row.ProjectId == owner.ProjectId, Ct);
            finding.Status = AnalysisFindingStatus.Accepted;
            db.AnalysisFindingDecisions.Add(new AnalysisFindingDecision
            {
                OrganizationId = owner.OrganizationId,
                ProjectId = owner.ProjectId,
                AnalysisFindingId = finding.Id,
                Status = AnalysisFindingStatus.Accepted,
                Reason = "Accepted until the vendor ships a fix",
                CreatedByUsername = "tester",
                ExpiresAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync(Ct);
        }

        await PassAsync();
        Assert.Single(await ReportsAsync(owner.ProjectId));

        item = await MoveItemAsync(owner, item, "lib", "2.0.0");
        await PassAsync();
        Assert.Equal(AnalysisFindingStatus.Fixed, Assert.Single(await FindingsAsync(owner.ProjectId)).Status);

        await MoveItemAsync(owner, item, "lib", "1.0.0");
        await PassAsync();

        Assert.Equal(AnalysisFindingStatus.Accepted, Assert.Single(await FindingsAsync(owner.ProjectId)).Status);
    }

    [Fact]
    public async Task AWithdrawnAdvisory_ClosesTheMatchAndItsFinding()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));
        await PassAsync();
        Assert.Equal(AnalysisFindingStatus.Open, Assert.Single(await FindingsAsync(owner.ProjectId)).Status);

        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix, revision: 1, withdrawn: true));
        await PassAsync();

        Assert.Equal(AnalysisFindingStatus.Fixed, Assert.Single(await FindingsAsync(owner.ProjectId)).Status);
        Assert.NotNull(Assert.Single(await MatchesAsync(owner.ProjectId)).ClosedAt);
        Assert.Equal(2, (await ReportsAsync(owner.ProjectId)).Count);
    }

    [Fact]
    public async Task AToVerifyMatch_StaysVisibleAsSuch_AndIsNeverClosedAsFixedByDefault()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", Unreadable));

        await PassAsync();
        await PassAsync();

        var finding = Assert.Single(await FindingsAsync(owner.ProjectId));
        Assert.Equal(AnalysisFindingStatus.Open, finding.Status);
        Assert.Equal(AnalysisConfidence.Low, finding.Confidence);
        var match = Assert.Single(await MatchesAsync(owner.ProjectId));
        Assert.Equal(VulnerabilityMatchStatus.ToVerify, match.Status);
        Assert.Null(match.ClosedAt);
        Assert.Single(await ReportsAsync(owner.ProjectId));

        // The advisory is corrected and now covers the version: the same finding is certain, which is a change to publish.
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix, revision: 1));
        await PassAsync();

        var certain = Assert.Single(await FindingsAsync(owner.ProjectId));
        Assert.Equal(finding.Id, certain.Id);
        Assert.Equal(AnalysisConfidence.High, certain.Confidence);
        Assert.Equal(VulnerabilityMatchStatus.Affected, Assert.Single(await MatchesAsync(owner.ProjectId)).Status);
        Assert.Equal(2, (await ReportsAsync(owner.ProjectId)).Count);
    }

    [Fact]
    public async Task TwoProjects_AreIsolated_EachHasItsOwnReportAndFindings()
    {
        await ResetAndMigrateAsync();
        var alpha = await SeedProjectAsync("Alpha");
        var beta = await SeedProjectAsync("Beta");
        var gamma = await SeedProjectAsync("Gamma");
        var alphaItem = await AddItemAsync(alpha, "lib", "1.0.0");
        await AddItemAsync(beta, "lib", "1.1.0");
        await AddItemAsync(gamma, "lib", "3.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));

        await PassAsync();

        var alphaFinding = Assert.Single(await FindingsAsync(alpha.ProjectId));
        var betaFinding = Assert.Single(await FindingsAsync(beta.ProjectId));
        Assert.NotEqual(alphaFinding.Fingerprint, betaFinding.Fingerprint);
        Assert.Equal(alpha.OrganizationId, alphaFinding.OrganizationId);
        Assert.Equal(beta.OrganizationId, betaFinding.OrganizationId);
        Assert.Equal(alphaFinding.Id, Assert.Single(await MatchesAsync(alpha.ProjectId)).AnalysisFindingId);
        Assert.Equal(betaFinding.Id, Assert.Single(await MatchesAsync(beta.ProjectId)).AnalysisFindingId);
        Assert.Equal(alphaFinding.Id, Assert.Single(Assert.Single(await ReportsAsync(alpha.ProjectId)).Occurrences).AnalysisFindingId);
        Assert.Equal(betaFinding.Id, Assert.Single(Assert.Single(await ReportsAsync(beta.ProjectId)).Occurrences).AnalysisFindingId);
        Assert.Empty(await FindingsAsync(gamma.ProjectId));
        Assert.Empty(await ReportsAsync(gamma.ProjectId));

        await MoveItemAsync(alpha, alphaItem, "lib", "2.0.0");
        await PassAsync();

        Assert.Equal(AnalysisFindingStatus.Fixed, Assert.Single(await FindingsAsync(alpha.ProjectId)).Status);
        Assert.Equal(AnalysisFindingStatus.Open, Assert.Single(await FindingsAsync(beta.ProjectId)).Status);
        Assert.Single(await ReportsAsync(beta.ProjectId));
        Assert.Empty(await ReportsAsync(gamma.ProjectId));
    }

    [Fact]
    public async Task APublicationOverTheQuota_LeavesTheWorkPending_AndTheNextPassPublishesIt()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));
        await using (var db = NewContext())
        {
            // One report of the last 24 hours, from a pipeline scanner, uses the whole quota of this test.
            db.AnalysisReports.Add(new AnalysisReport
            {
                OrganizationId = owner.OrganizationId,
                ProjectId = owner.ProjectId,
                ScannerKey = "trivy-dependencies",
                ScannerName = "Trivy",
                ScannerVersion = "1",
                Category = AnalysisCategory.Dependencies,
                Status = AnalysisReportStatus.Passed,
                Format = AnalysisReportFormat.Sarif,
                ContentHash = new string('a', 64),
                StartedAt = _clock.GetUtcNow().UtcDateTime,
                CompletedAt = _clock.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync(Ct);
        }

        await PassAsync(new AnalysisPlatformOptions { ProjectDailyReportLimit = 1 });

        Assert.Empty(await ReportsAsync(owner.ProjectId));
        Assert.Empty(await FindingsAsync(owner.ProjectId));
        var pending = Assert.Single(await MatchesAsync(owner.ProjectId));
        Assert.Null(pending.AnalysisFindingId);
        Assert.Null(pending.ClosedAt);

        await PassAsync();

        Assert.Single(await ReportsAsync(owner.ProjectId));
        Assert.Equal(Assert.Single(await FindingsAsync(owner.ProjectId)).Id, Assert.Single(await MatchesAsync(owner.ProjectId)).AnalysisFindingId);
    }

    [Fact]
    public async Task APublicationThatFailsToCommit_LeavesNoReportAndNoFinding_AndTheNextPassPublishesIt()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var item = await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));

        await PassAsync(failCommit: true);

        Assert.Empty(await ReportsAsync(owner.ProjectId));
        Assert.Empty(await FindingsAsync(owner.ProjectId));
        Assert.Null(Assert.Single(await MatchesAsync(owner.ProjectId)).AnalysisFindingId);

        await PassAsync();
        Assert.Single(await ReportsAsync(owner.ProjectId));
        Assert.Equal(AnalysisFindingStatus.Open, Assert.Single(await FindingsAsync(owner.ProjectId)).Status);

        // The same holds for a closure: the fix is not lost when its publication fails.
        await MoveItemAsync(owner, item, "lib", "2.0.0");
        await PassAsync(failCommit: true);

        Assert.Equal(AnalysisFindingStatus.Open, Assert.Single(await FindingsAsync(owner.ProjectId)).Status);
        Assert.Single(await ReportsAsync(owner.ProjectId));

        await PassAsync();

        Assert.Equal(AnalysisFindingStatus.Fixed, Assert.Single(await FindingsAsync(owner.ProjectId)).Status);
        Assert.Equal(2, (await ReportsAsync(owner.ProjectId)).Count);
    }
}
