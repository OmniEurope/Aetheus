// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// The analysis insights read model: the Dependency-Track tracking projects, the vulnerability
/// observation listing, the ingest accounting, the operational snapshot, the cross-scanner
/// "fixed" reconciliation and the metric/component listings with their baselines.
///
/// The reconciliation case is the one that matters most: a finding must only be marked fixed when
/// no other scanner still reports it in its own latest report, so the tests seed both outcomes.
/// </summary>
public sealed class AnalysisInsightsRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime NowUtc = Now.UtcDateTime;

    /// <summary>
    /// Starts well before <see cref="Now"/> because <see cref="AppDbContext"/> stamps
    /// <c>CreatedAt</c> from this clock and a <see cref="FakeTimeProvider"/> only moves forward, so
    /// the retention-window cases have to insert their "too old" rows first.
    /// </summary>
    private readonly FakeTimeProvider _clock = new(Now.AddDays(-30));
    private readonly AppDbContext _db;
    private readonly AnalysisInsightsRepository _repository;

    public AnalysisInsightsRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options, _clock);
        _repository = new AnalysisInsightsRepository(_db);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SaveAsync() => await _db.SaveChangesAsync(Ct);

    /// <summary>Inserts one row as if the wall clock read <paramref name="instant"/>.</summary>
    private async Task SaveAtAsync(DateTime instant, object entity)
    {
        _clock.SetUtcNow(new DateTimeOffset(instant));
        _db.Add(entity);
        await _db.SaveChangesAsync(Ct);
    }

    private static AnalysisTrackingProject Tracking(
        int id, int projectId, bool active = true, DateTime? lastSyncAt = null,
        string syncStatus = "Synced", string? lastError = null) =>
        new()
        {
            Id = id,
            OrganizationId = 7,
            ProjectId = projectId,
            ExternalProjectId = $"ext-{id}",
            Active = active,
            SyncStatus = syncStatus,
            LastError = lastError,
            LastSyncAt = lastSyncAt
        };

    private static AnalysisVulnerabilityObservation Observation(
        int id, int projectId, string vulnerabilityId, string componentName,
        AnalysisSeverity severity = AnalysisSeverity.High, DateTime? observedAt = null,
        string? packageUrl = null) =>
        new()
        {
            Id = id,
            OrganizationId = 7,
            ProjectId = projectId,
            AnalysisTrackingProjectId = 1,
            VulnerabilityId = vulnerabilityId,
            ComponentName = componentName,
            ComponentVersion = "1.0.0",
            PackageUrl = packageUrl,
            Severity = severity,
            Status = "Open",
            ObservedAt = observedAt ?? NowUtc
        };

    private static AnalysisReport Report(
        int id, int projectId, string scannerKey = "trivy",
        AnalysisCategory category = AnalysisCategory.Sast, string? branch = "main",
        int? pipelineRunId = null, long contentSize = 0, DateTime? completedAt = null) =>
        new()
        {
            Id = id,
            OrganizationId = 7,
            ProjectId = projectId,
            ScannerKey = scannerKey,
            ScannerName = scannerKey,
            Category = category,
            BranchName = branch,
            PipelineRunId = pipelineRunId,
            ContentSize = contentSize,
            CompletedAt = completedAt ?? NowUtc
        };

    // ---------- tracking projects ----------

    [Fact]
    public async Task GetTrackingProjectAsync_MatchesOnTheProjectOrReturnsNull()
    {
        _db.AnalysisTrackingProjects.AddRange(Tracking(1, 10), Tracking(2, 11));
        await SaveAsync();

        Assert.Equal(2, (await _repository.GetTrackingProjectAsync(11, Ct))!.Id);
        Assert.Null(await _repository.GetTrackingProjectAsync(12, Ct));
    }

    [Fact]
    public async Task GetActiveTrackingProjectsAsync_TakesTheStalestActiveProjectsFirst()
    {
        _db.AnalysisTrackingProjects.AddRange(
            Tracking(1, 10, lastSyncAt: NowUtc.AddHours(-1)),
            Tracking(2, 11, lastSyncAt: NowUtc.AddDays(-1)),
            Tracking(3, 12, lastSyncAt: NowUtc),
            Tracking(4, 13, active: false, lastSyncAt: NowUtc.AddDays(-10)));
        await SaveAsync();

        var due = await _repository.GetActiveTrackingProjectsAsync(2, Ct);

        Assert.Equal([11, 10], due.Select(item => item.ProjectId));
    }

    [Fact]
    public async Task GetActiveTrackingProjectsAsync_ClampsAnAbsurdLimitToAtLeastOne()
    {
        _db.AnalysisTrackingProjects.AddRange(Tracking(1, 10), Tracking(2, 11));
        await SaveAsync();

        Assert.Single(await _repository.GetActiveTrackingProjectsAsync(0, Ct));
        Assert.Equal(2, (await _repository.GetActiveTrackingProjectsAsync(5000, Ct)).Count);
    }

    [Fact]
    public async Task SaveTrackingSnapshotAsync_InsertsANewProjectWithItsObservations()
    {
        await _repository.SaveTrackingSnapshotAsync(
            Tracking(0, 10),
            [Observation(0, 10, "CVE-2026-1", "serilog")],
            Ct);
        _db.ChangeTracker.Clear();

        Assert.Single(_db.AnalysisTrackingProjects);
        Assert.Single(_db.AnalysisVulnerabilityObservations);
    }

    [Fact]
    public async Task SaveTrackingSnapshotAsync_UpdatesAnExistingProjectWithoutInsertingADuplicate()
    {
        _db.AnalysisTrackingProjects.Add(Tracking(1, 10));
        await SaveAsync();
        var tracked = await _db.AnalysisTrackingProjects.FirstAsync(Ct);
        tracked.SyncStatus = "Error";

        await _repository.SaveTrackingSnapshotAsync(tracked, [], Ct);
        _db.ChangeTracker.Clear();

        Assert.Equal("Error", (await _db.AnalysisTrackingProjects.AsNoTracking().FirstAsync(Ct)).SyncStatus);
        Assert.Single(_db.AnalysisTrackingProjects);
    }

    // ---------- vulnerability observations ----------

    [Fact]
    public async Task GetVulnerabilityObservationsAsync_SearchesTheIdTheComponentAndThePackageUrl()
    {
        _db.AnalysisVulnerabilityObservations.AddRange(
            Observation(1, 10, "CVE-2026-NEEDLE", "alpha"),
            Observation(2, 10, "CVE-2026-2", "needle-lib"),
            Observation(3, 10, "CVE-2026-3", "gamma", packageUrl: "pkg:nuget/Needle@1.0"),
            Observation(4, 10, "CVE-2026-4", "delta"),
            Observation(5, 11, "CVE-2026-NEEDLE", "other-project"));
        await SaveAsync();

        var (items, total) = await _repository.GetVulnerabilityObservationsAsync(
            10, new PaginationRequest { Search = "  needle  " }, Ct);

        Assert.Equal(3, total);
        Assert.Equal([1, 2, 3], items.Select(item => item.Id).Order());
    }

    [Theory]
    [InlineData("Severity", false, new[] { 2, 1 })]
    [InlineData("Severity", true, new[] { 1, 2 })]
    [InlineData("VulnerabilityId", false, new[] { 1, 2 })]
    [InlineData("VulnerabilityId", true, new[] { 2, 1 })]
    [InlineData("ObservedAt", false, new[] { 1, 2 })]
    [InlineData(null, false, new[] { 2, 1 })]
    public async Task GetVulnerabilityObservationsAsync_SortsOnEverySupportedKey(
        string? sortBy, bool descending, int[] expected)
    {
        _db.AnalysisVulnerabilityObservations.AddRange(
            Observation(1, 10, "CVE-A", "alpha", AnalysisSeverity.Critical, NowUtc),
            Observation(2, 10, "CVE-B", "beta", AnalysisSeverity.Low, NowUtc.AddHours(1)));
        await SaveAsync();

        var (items, _) = await _repository.GetVulnerabilityObservationsAsync(
            10, new PaginationRequest { SortBy = sortBy, SortDescending = descending }, Ct);

        Assert.Equal(expected, items.Select(item => item.Id));
    }

    [Fact]
    public async Task GetVulnerabilityObservationsAsync_PagesAfterOrdering()
    {
        _db.AnalysisVulnerabilityObservations.AddRange(Enumerable.Range(1, 5)
            .Select(index => Observation(index, 10, $"CVE-{index:D2}", "alpha",
                observedAt: NowUtc.AddMinutes(index))));
        await SaveAsync();

        var (items, total) = await _repository.GetVulnerabilityObservationsAsync(
            10, new PaginationRequest { Page = 2, PageSize = 2 }, Ct);

        Assert.Equal(5, total);
        Assert.Equal([3, 2], items.Select(item => item.Id));
    }

    // ---------- ingest accounting and operational snapshot ----------

    [Fact]
    public async Task GetProjectIngestUsageAsync_CountsAndSumsOnlyTheReportsInsideTheWindow()
    {
        await SaveAtAsync(NowUtc.AddHours(-2), Report(1, 10, contentSize: 100));
        await SaveAtAsync(NowUtc.AddMinutes(-30), Report(2, 10, contentSize: 200));
        await SaveAtAsync(NowUtc.AddMinutes(-10), Report(3, 10, contentSize: 300));
        await SaveAtAsync(NowUtc, Report(4, 11, contentSize: 999));
        _db.ChangeTracker.Clear();

        Assert.Equal((2, 500L), await _repository.GetProjectIngestUsageAsync(10, NowUtc.AddHours(-1), Ct));
    }

    [Fact]
    public async Task GetProjectIngestUsageAsync_ReportsZeroBytesWhenNothingWasIngested()
    {
        Assert.Equal((0, 0L), await _repository.GetProjectIngestUsageAsync(10, NowUtc, Ct));
    }

    [Fact]
    public async Task GetOperationalSnapshotAsync_ReportsTrackingHealthScannerHostsAndStorage()
    {
        _db.AnalysisTrackingProjects.AddRange(
            Tracking(1, 10, syncStatus: "Error", lastError: "401 from Dependency-Track"),
            Tracking(2, 11, active: false));
        _db.Servers.AddRange(
            new Server { Id = 1, Name = "scanner", Hostname = "scanner", ScannerCapabilitiesJson = "[\"trivy\"]", LastHeartbeat = NowUtc.AddMinutes(-5) },
            new Server { Id = 2, Name = "plain", Hostname = "plain", LastHeartbeat = NowUtc.AddMinutes(-5) });
        await SaveAsync();
        // Oldest first: the fake clock only moves forward, and report 3 must fall outside the window.
        await SaveAtAsync(NowUtc.AddHours(-2), Report(3, 10, contentSize: 999));
        await SaveAtAsync(NowUtc.AddMinutes(-10), Report(1, 10, contentSize: 100));
        await SaveAtAsync(NowUtc.AddMinutes(-5), Report(2, 10, contentSize: 200));
        _db.ChangeTracker.Clear();

        var snapshot = await _repository.GetOperationalSnapshotAsync(NowUtc.AddHours(-1), Ct);

        var tracking = Assert.Single(snapshot.TrackingProjects);
        Assert.Equal("Error", tracking.SyncStatus);
        Assert.Equal("401 from Dependency-Track", tracking.LastError);
        var server = Assert.Single(snapshot.Servers);
        Assert.Equal("scanner", server.ServerName);
        var storage = Assert.Single(snapshot.Storage);
        Assert.Equal(2, storage.ReportCount);
        Assert.Equal(300, storage.ContentBytes);
    }

    /// <summary>
    /// The capabilities column keeps whatever the agent last said and is never cleared, so a runner that
    /// stopped reporting would otherwise raise the same operational alert forever, about a workspace that
    /// may no longer exist and that no operator can act on.
    /// </summary>
    [Fact]
    public async Task GetOperationalSnapshotAsync_IgnoresAServerThatStoppedReporting()
    {
        _db.Servers.AddRange(
            new Server
            {
                Id = 1,
                Name = "live",
                Hostname = "live",
                ScannerCapabilitiesJson = "[\"scanner-cleanup:degraded:2 residual workspaces\"]",
                LastHeartbeat = NowUtc.AddMinutes(-5)
            },
            new Server
            {
                Id = 2,
                Name = "retired",
                Hostname = "retired",
                ScannerCapabilitiesJson = "[\"scanner-cleanup:degraded:2 residual workspaces\"]",
                LastHeartbeat = NowUtc.AddDays(-30)
            });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var snapshot = await _repository.GetOperationalSnapshotAsync(NowUtc.AddHours(-1), Ct);

        var server = Assert.Single(snapshot.Servers);
        Assert.Equal("live", server.ServerName);
    }

    // ---------- cross-scanner reconciliation ----------

    /// <summary>
    /// One open finding seen by <paramref name="scannerKey"/> on <c>main</c>, plus the report/occurrence
    /// rows needed for the "is another scanner still reporting it?" question.
    /// </summary>
    private async Task SeedOpenFindingAsync(string scannerKey = "trivy")
    {
        _db.AnalysisFindings.Add(new AnalysisFinding
        {
            Id = 1,
            OrganizationId = 7,
            ProjectId = 10,
            Fingerprint = "fp-1",
            Category = AnalysisCategory.Sast,
            Status = AnalysisFindingStatus.Open,
            Title = "hardcoded secret"
        });
        _db.AnalysisReports.Add(Report(1, 10, scannerKey));
        _db.AnalysisFindingOccurrences.Add(new AnalysisFindingOccurrence
        {
            Id = 1,
            AnalysisReportId = 1,
            AnalysisFindingId = 1,
            ScannerKey = scannerKey,
            BranchName = "main"
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task MarkMissingFindingsFixedAsync_ClosesAFindingNoScannerReportsAnyMore()
    {
        await SeedOpenFindingAsync();

        await _repository.MarkMissingFindingsFixedAsync(
            10, "trivy", AnalysisCategory.Sast, "main", [], NowUtc, Ct);
        _db.ChangeTracker.Clear();

        var finding = await _db.AnalysisFindings.AsNoTracking().FirstAsync(Ct);
        Assert.Equal(AnalysisFindingStatus.Fixed, finding.Status);
        Assert.Equal(NowUtc, finding.ResolvedAt);
    }

    [Theory]
    [InlineData(AnalysisFindingStatus.Accepted)]
    [InlineData(AnalysisFindingStatus.FalsePositive)]
    [InlineData(AnalysisFindingStatus.Mitigated)]
    public async Task MarkMissingFindingsFixedAsync_FixesADecidedFindingToo(AnalysisFindingStatus decided)
    {
        // A finding absent from the latest scan becomes Fixed regardless of its decision history.
        await SeedOpenFindingAsync();
        var seeded = await _db.AnalysisFindings.FirstAsync(Ct);
        seeded.Status = decided;
        await SaveAsync();
        _db.ChangeTracker.Clear();

        await _repository.MarkMissingFindingsFixedAsync(
            10, "trivy", AnalysisCategory.Sast, "main", [], NowUtc, Ct);
        _db.ChangeTracker.Clear();

        Assert.Equal(AnalysisFindingStatus.Fixed, (await _db.AnalysisFindings.AsNoTracking().FirstAsync(Ct)).Status);
    }

    [Fact]
    public async Task MarkMissingFindingsFixedAsync_LeavesAFindingTheCurrentScanStillObservesOpen()
    {
        await SeedOpenFindingAsync();

        await _repository.MarkMissingFindingsFixedAsync(
            10, "trivy", AnalysisCategory.Sast, "main", ["fp-1"], NowUtc, Ct);
        _db.ChangeTracker.Clear();

        Assert.Equal(
            AnalysisFindingStatus.Open,
            (await _db.AnalysisFindings.AsNoTracking().FirstAsync(Ct)).Status);
    }

    [Fact]
    public async Task MarkMissingFindingsFixedAsync_KeepsAFindingAnotherScannerStillReportsOpen()
    {
        await SeedOpenFindingAsync();
        // A second scanner saw the same finding on the same branch, and its own latest report still
        // carries it: the trivy run going quiet must not close it.
        _db.AnalysisReports.Add(Report(2, 10, "gitleaks", completedAt: NowUtc.AddMinutes(5)));
        _db.AnalysisFindingOccurrences.Add(new AnalysisFindingOccurrence
        {
            Id = 2,
            AnalysisReportId = 2,
            AnalysisFindingId = 1,
            ScannerKey = "gitleaks",
            BranchName = "main"
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        await _repository.MarkMissingFindingsFixedAsync(
            10, "trivy", AnalysisCategory.Sast, "main", [], NowUtc, Ct);
        _db.ChangeTracker.Clear();

        Assert.Equal(
            AnalysisFindingStatus.Open,
            (await _db.AnalysisFindings.AsNoTracking().FirstAsync(Ct)).Status);
    }

    [Fact]
    public async Task MarkMissingFindingsFixedAsync_ClosesAFindingTheOtherScannerDroppedInItsLatestReport()
    {
        await SeedOpenFindingAsync();
        // gitleaks used to report it, but its newest report no longer does.
        _db.AnalysisReports.AddRange(
            Report(2, 10, "gitleaks", completedAt: NowUtc.AddMinutes(-10)),
            Report(3, 10, "gitleaks", completedAt: NowUtc.AddMinutes(5)));
        _db.AnalysisFindingOccurrences.Add(new AnalysisFindingOccurrence
        {
            Id = 2,
            AnalysisReportId = 2,
            AnalysisFindingId = 1,
            ScannerKey = "gitleaks",
            BranchName = "main"
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        await _repository.MarkMissingFindingsFixedAsync(
            10, "trivy", AnalysisCategory.Sast, "main", [], NowUtc, Ct);
        _db.ChangeTracker.Clear();

        Assert.Equal(
            AnalysisFindingStatus.Fixed,
            (await _db.AnalysisFindings.AsNoTracking().FirstAsync(Ct)).Status);
    }

    [Fact]
    public async Task MarkMissingFindingsFixedAsync_IsANoOpWhenNothingMatchesTheScannerAndBranch()
    {
        await SeedOpenFindingAsync();

        await _repository.MarkMissingFindingsFixedAsync(
            10, "trivy", AnalysisCategory.Sast, "release", [], NowUtc, Ct);
        _db.ChangeTracker.Clear();

        Assert.Equal(
            AnalysisFindingStatus.Open,
            (await _db.AnalysisFindings.AsNoTracking().FirstAsync(Ct)).Status);
    }

    // ---------- metrics ----------

    [Fact]
    public async Task GetBaselineMetricValuesAsync_ShortCircuitsOnAnEmptyKeySet()
    {
        Assert.Empty(await _repository.GetBaselineMetricValuesAsync(10, "main", 5, [], Ct));
    }

    [Fact]
    public async Task GetBaselineMetricValuesAsync_ReturnsNothingWhenNoSuccessfulBaselineRunExists()
    {
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "ci" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 5, PipelineId = 1, Status = PipelineStatus.Failed });
        _db.AnalysisReports.Add(Report(1, 10, branch: "main", pipelineRunId: 5));
        _db.AnalysisMetrics.Add(new AnalysisMetric
        {
            Id = 1,
            OrganizationId = 7,
            ProjectId = 10,
            AnalysisReportId = 1,
            Key = "coverage",
            Value = 80
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.Empty(await _repository.GetBaselineMetricValuesAsync(10, "main", 9, ["coverage"], Ct));
    }

    [Fact]
    public async Task GetBaselineMetricValuesAsync_TakesTheNewestSuccessfulBaselineRunAndKeysByIdentity()
    {
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "ci" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 5, PipelineId = 1, Status = PipelineStatus.Success },
            new PipelineRun { Id = 6, PipelineId = 1, Status = PipelineStatus.Success },
            new PipelineRun { Id = 9, PipelineId = 1, Status = PipelineStatus.Success });
        _db.AnalysisReports.AddRange(
            Report(1, 10, branch: "main", pipelineRunId: 5, completedAt: NowUtc.AddDays(-2)),
            Report(2, 10, branch: "main", pipelineRunId: 6, completedAt: NowUtc.AddDays(-1)),
            Report(3, 10, branch: "main", pipelineRunId: 9, completedAt: NowUtc));
        _db.AnalysisMetrics.AddRange(
            new AnalysisMetric { Id = 1, OrganizationId = 7, ProjectId = 10, AnalysisReportId = 1, Key = "coverage", Value = 70 },
            new AnalysisMetric { Id = 2, OrganizationId = 7, ProjectId = 10, AnalysisReportId = 2, Key = "coverage", Value = 80 },
            new AnalysisMetric { Id = 3, OrganizationId = 7, ProjectId = 10, AnalysisReportId = 3, Key = "coverage", Value = 99 });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var baseline = await _repository.GetBaselineMetricValuesAsync(10, "main", 9, ["coverage"], Ct);

        var only = Assert.Single(baseline);
        Assert.Equal(80, only.Value);
        Assert.Equal(
            AnalysisMetricIdentity.Build("coverage", null, null, null, null),
            only.Key,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMetricsAsync_FiltersOnKeyLanguageBranchAndFreeText()
    {
        _db.AnalysisReports.AddRange(
            Report(1, 10, branch: "main"),
            Report(2, 10, branch: "release"));
        _db.AnalysisMetrics.AddRange(
            Metric(1, reportId: 1, key: "coverage", language: "csharp", tool: "coverlet"),
            Metric(2, reportId: 1, key: "complexity", language: "csharp", tool: "roslyn"),
            Metric(3, reportId: 1, key: "coverage", language: "ts", tool: "vitest"),
            Metric(4, reportId: 2, key: "coverage", language: "csharp", tool: "coverlet"),
            Metric(5, reportId: 1, key: "coverage", language: "csharp", tool: "needle-tool"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var byKey = await _repository.GetMetricsAsync(10, new AnalysisMetricPaginationRequest { Key = "complexity" }, Ct);
        var byLanguage = await _repository.GetMetricsAsync(10, new AnalysisMetricPaginationRequest { Language = "ts" }, Ct);
        var byBranch = await _repository.GetMetricsAsync(10, new AnalysisMetricPaginationRequest { Branch = "release" }, Ct);
        var bySearch = await _repository.GetMetricsAsync(10, new AnalysisMetricPaginationRequest { Search = " needle " }, Ct);

        Assert.Equal([2], byKey.Items.Select(metric => metric.Id));
        Assert.Equal([3], byLanguage.Items.Select(metric => metric.Id));
        Assert.Equal([4], byBranch.Items.Select(metric => metric.Id));
        Assert.Equal([5], bySearch.Items.Select(metric => metric.Id));
    }

    [Fact]
    public async Task GetMetricsAsync_WithLatestReportOnlyKeepsASingleReport()
    {
        _db.AnalysisReports.AddRange(
            Report(1, 10, completedAt: NowUtc.AddDays(-1)),
            Report(2, 10, completedAt: NowUtc));
        _db.AnalysisMetrics.AddRange(
            Metric(1, reportId: 1, key: "coverage"),
            Metric(2, reportId: 2, key: "coverage"),
            Metric(3, reportId: 2, key: "complexity"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var (items, total) = await _repository.GetMetricsAsync(
            10, new AnalysisMetricPaginationRequest { LatestReportOnly = true }, Ct);

        Assert.Equal(2, total);
        Assert.Equal([2, 3], items.Select(metric => metric.Id).Order());
    }

    [Fact]
    public async Task GetMetricsAsync_WithLatestReportOnlyReturnsNothingWhenTheProjectHasNoMetric()
    {
        var (items, total) = await _repository.GetMetricsAsync(
            10, new AnalysisMetricPaginationRequest { LatestReportOnly = true }, Ct);

        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    [Theory]
    [InlineData("Key", false, new[] { 2, 1 })]
    [InlineData("Key", true, new[] { 1, 2 })]
    [InlineData("Value", false, new[] { 1, 2 })]
    [InlineData("Value", true, new[] { 2, 1 })]
    [InlineData("CreatedAt", false, new[] { 1, 2 })]
    [InlineData(null, false, new[] { 2, 1 })]
    public async Task GetMetricsAsync_SortsOnEverySupportedKey(
        string? sortBy, bool descending, int[] expected)
    {
        _db.AnalysisReports.Add(Report(1, 10));
        await SaveAsync();
        await SaveAtAsync(NowUtc, Metric(1, reportId: 1, key: "zulu", value: 1));
        await SaveAtAsync(NowUtc.AddHours(1), Metric(2, reportId: 1, key: "alpha", value: 2));
        _db.ChangeTracker.Clear();

        var (items, _) = await _repository.GetMetricsAsync(
            10, new AnalysisMetricPaginationRequest { SortBy = sortBy, SortDescending = descending }, Ct);

        Assert.Equal(expected, items.Select(metric => metric.Id));
    }

    // ---------- components ----------

    [Fact]
    public async Task GetComponentsAsync_FiltersOnTypeBranchAndFreeText()
    {
        _db.AnalysisReports.AddRange(Report(1, 10, branch: "main"), Report(2, 10, branch: "release"));
        _db.AnalysisComponents.AddRange(
            Component(1, reportId: 1, name: "serilog", type: "library"),
            Component(2, reportId: 1, name: "node", type: "framework"),
            Component(3, reportId: 2, name: "serilog", type: "library"),
            Component(4, reportId: 1, name: "needle-pkg", type: "library"),
            Component(5, reportId: 1, name: "other", type: "library", packageUrl: "pkg:nuget/Needle@2"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var byType = await _repository.GetComponentsAsync(
            10, new AnalysisComponentPaginationRequest { ComponentType = "framework" }, Ct);
        var byBranch = await _repository.GetComponentsAsync(
            10, new AnalysisComponentPaginationRequest { Branch = "release" }, Ct);
        var bySearch = await _repository.GetComponentsAsync(
            10, new AnalysisComponentPaginationRequest { Search = "needle" }, Ct);

        Assert.Equal([2], byType.Items.Select(component => component.Id));
        Assert.Equal([3], byBranch.Items.Select(component => component.Id));
        Assert.Equal([4, 5], bySearch.Items.Select(component => component.Id).Order());
    }

    [Theory]
    [InlineData("Name", false, new[] { 2, 1 })]
    [InlineData("Name", true, new[] { 1, 2 })]
    [InlineData("Version", false, new[] { 1, 2 })]
    [InlineData("Version", true, new[] { 2, 1 })]
    [InlineData(null, false, new[] { 2, 1 })]
    public async Task GetComponentsAsync_SortsOnEverySupportedKey(
        string? sortBy, bool descending, int[] expected)
    {
        _db.AnalysisReports.Add(Report(1, 10));
        _db.AnalysisComponents.AddRange(
            Component(1, reportId: 1, name: "zulu", version: "1.0.0"),
            Component(2, reportId: 1, name: "alpha", version: "2.0.0"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var (items, _) = await _repository.GetComponentsAsync(
            10, new AnalysisComponentPaginationRequest { SortBy = sortBy, SortDescending = descending }, Ct);

        Assert.Equal(expected, items.Select(component => component.Id));
    }

    [Fact]
    public async Task GetLatestProjectRunIdAsync_ReturnsTheNewestRunThatProducedAReport()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "ci", ProjectId = 10 },
            new Pipeline { Id = 2, Name = "other", ProjectId = 11 });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 5, PipelineId = 1, StartedAt = NowUtc.AddDays(-1) },
            new PipelineRun { Id = 6, PipelineId = 1, StartedAt = NowUtc },
            new PipelineRun { Id = 7, PipelineId = 2, StartedAt = NowUtc.AddDays(1) });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(6, await _repository.GetLatestProjectRunIdAsync(10, Ct));
        Assert.Equal(7, await _repository.GetLatestProjectRunIdAsync(11, Ct));
        Assert.Null(await _repository.GetLatestProjectRunIdAsync(12, Ct));
    }

    private static AnalysisMetric Metric(
        int id, int reportId, string key, double value = 1, string? language = null,
        string tool = "tool") =>
        new()
        {
            Id = id,
            OrganizationId = 7,
            ProjectId = 10,
            AnalysisReportId = reportId,
            Key = key,
            Value = value,
            Language = language,
            ToolName = tool
        };

    private static AnalysisComponent Component(
        int id, int reportId, string name, string version = "1.0.0", string? type = null,
        string? packageUrl = null) =>
        new()
        {
            Id = id,
            OrganizationId = 7,
            ProjectId = 10,
            AnalysisReportId = reportId,
            Name = name,
            Version = version,
            ComponentType = type,
            PackageUrl = packageUrl
        };

    public void Dispose() => _db.Dispose();
}
