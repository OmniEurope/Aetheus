// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

public sealed class AnalysisServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AnalysisService _service;
    private readonly IDependencyTrackOutbox _dependencyTrackOutbox;
    private readonly FakeTimeProvider _time;
    private readonly DateTime _now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    public AnalysisServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _time = new FakeTimeProvider(new DateTimeOffset(_now));
        _db = new AppDbContext(options, _time);
        var organization = new Organization { Id = 1, Name = "Aetheus", Slug = "aetheus" };
        var project = new Project { Id = 1, Name = "Project", OrganizationId = 1, Organization = organization, DefaultBranch = "main" };
        var pipeline = new Pipeline { Id = 1, Name = "security", ProjectId = 1, Project = project };
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 1, PipelineId = 1, Pipeline = pipeline, Status = PipelineStatus.Success, BranchName = "main", CommitHash = new string('a', 40) },
            new PipelineRun { Id = 2, PipelineId = 1, Pipeline = pipeline, Status = PipelineStatus.Running, BranchName = "feature", CommitHash = new string('b', 40) },
            new PipelineRun { Id = 3, PipelineId = 1, Pipeline = pipeline, Status = PipelineStatus.Running, BranchName = "main", CommitHash = new string('c', 40) },
            new PipelineRun { Id = 4, PipelineId = 1, Pipeline = pipeline, Status = PipelineStatus.Running, BranchName = "feature", CommitHash = new string('d', 40) },
            new PipelineRun { Id = 5, PipelineId = 1, Pipeline = pipeline, Status = PipelineStatus.Running, BranchName = "main", CommitHash = new string('e', 40) },
            new PipelineRun { Id = 6, PipelineId = 1, Pipeline = pipeline, Status = PipelineStatus.Running, BranchName = "main", CommitHash = new string('f', 40) });
        _db.PipelineArtifacts.AddRange(Enumerable.Range(1, 6).Select(runId => new PipelineArtifact
        {
            Id = 100 + runId,
            PipelineId = 1,
            PipelineRunId = runId,
            Name = $"analysis-{runId}",
            FilePath = $"analysis-{runId}.zip"
        }));
        _db.SaveChanges();
        var repository = new AnalysisRepository(_db);
        _dependencyTrackOutbox = Substitute.For<IDependencyTrackOutbox>();
        _service = new AnalysisService(
            repository,
            new AnalysisIngestGate(Options.Create(new AnalysisPlatformOptions())),
            new AnalysisPolicyEngine(repository),
            _time,
            Substitute.For<IAuditService>(),
            Substitute.For<IDbTransactionScope>(),
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<INotificationService>(),
            _dependencyTrackOutbox,
            Options.Create(new AnalysisPlatformOptions()),
            Options.Create(new DependencyTrackOptions()));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task PublishReportAsync_MarksMissingFindingFixedOnReferenceScan()
    {
        await _service.PublishReportAsync(1, Request(), TestContext.Current.CancellationToken);
        var empty = Request() with { ReportContent = EmptySarif(), CompletedAt = _now.AddMinutes(2) };

        await _service.PublishReportAsync(3, empty with { PipelineArtifactId = 103 }, TestContext.Current.CancellationToken);

        var finding = Assert.Single(_db.AnalysisFindings);
        Assert.Equal(AnalysisFindingStatus.Fixed, finding.Status);
        Assert.NotNull(finding.ResolvedAt);
    }

    [Fact]
    public async Task GetRunGateAsync_FailsClosedWhenARequiredScannerPublishedNoReport()
    {
        await _service.PublishReportAsync(1, Request(), TestContext.Current.CancellationToken);
        _db.Tasks.AddRange(
            new ServerTask
            {
                Id = 900,
                ServerId = 1,
                PipelineRunId = 1,
                Operation = OperationKind.PipelineRunScanner,
                Command = "opengrep",
                Name = "OpenGrep"
            },
            new ServerTask
            {
                Id = 901,
                ServerId = 1,
                PipelineRunId = 1,
                Operation = OperationKind.PipelineRunScanner,
                Command = "trivy-dependencies",
                Name = "Trivy"
            });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(
            1, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        Assert.DoesNotContain(gate.MissingProducers, item => item.StartsWith("opengrep:", StringComparison.Ordinal));
        Assert.Contains(gate.MissingProducers, item => item.StartsWith("trivy-dependencies:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRunGateAsync_ReturnsEveryFindingWithoutSilentTruncation()
    {
        var report = new AnalysisReport
        {
            Id = 700,
            OrganizationId = 1,
            ProjectId = 1,
            PipelineRunId = 1,
            PipelineArtifactId = 101,
            ScannerKey = "opengrep",
            ScannerName = "OpenGrep",
            ScannerVersion = "1.22.0",
            Category = AnalysisCategory.Sast,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.Sarif,
            ContentHash = new string('7', 64),
            StartedAt = _now.AddMinutes(-1),
            CompletedAt = _now,
            CreatedAt = _now
        };
        var findings = Enumerable.Range(1, 501).Select(index => new AnalysisFinding
        {
            Id = index,
            OrganizationId = 1,
            ProjectId = 1,
            Fingerprint = $"finding-{index}",
            RuleId = $"rule-{index}",
            Category = AnalysisCategory.Sast,
            Severity = AnalysisSeverity.Low,
            Confidence = AnalysisConfidence.High,
            Title = $"Finding {index}",
            Message = $"Evidence {index}",
            FirstSeenAt = _now,
            LastSeenAt = _now,
            CreatedAt = _now,
            UpdatedAt = _now
        }).ToList();
        var occurrences = findings.Select((finding, index) => new AnalysisFindingOccurrence
        {
            Id = index + 1,
            AnalysisReport = report,
            AnalysisFinding = finding,
            LocationHash = $"location-{index + 1}",
            ToolName = "OpenGrep",
            ScannerKey = "opengrep",
            RuleId = finding.RuleId,
            FilePath = $"src/File{index + 1}.cs",
            StartLine = index + 1,
            Message = finding.Message,
            CreatedAt = _now
        }).ToList();
        _db.AnalysisReports.Add(report);
        _db.AnalysisFindings.AddRange(findings);
        _db.AnalysisFindingOccurrences.AddRange(occurrences);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(
            1, TestContext.Current.CancellationToken);

        Assert.Equal(501, gate.Findings.Count);
        Assert.Contains(gate.Findings, finding => finding.FindingId == 501);
    }

    [Fact]
    public async Task GetRunGateAsync_CountsUniqueFindingsAcrossReports()
    {
        var finding = new AnalysisFinding
        {
            Id = 600,
            OrganizationId = 1,
            ProjectId = 1,
            Fingerprint = "shared-finding",
            RuleId = "shared-rule",
            Category = AnalysisCategory.Sast,
            Severity = AnalysisSeverity.High,
            Confidence = AnalysisConfidence.High,
            Title = "Shared finding",
            Message = "Reported twice",
            FirstSeenAt = _now,
            LastSeenAt = _now,
            CreatedAt = _now,
            UpdatedAt = _now
        };
        var reports = Enumerable.Range(0, 2).Select(index => new AnalysisReport
        {
            Id = 710 + index,
            OrganizationId = 1,
            ProjectId = 1,
            PipelineRunId = 1,
            PipelineArtifactId = 110 + index,
            ScannerKey = $"scanner-{index}",
            ScannerName = $"Scanner {index}",
            ScannerVersion = "1.0.0",
            Category = AnalysisCategory.Sast,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.Sarif,
            ContentHash = new string((char)('8' + index), 64),
            StartedAt = _now.AddMinutes(-1),
            CompletedAt = _now,
            CreatedAt = _now
        }).ToList();
        _db.AnalysisFindings.Add(finding);
        _db.AnalysisReports.AddRange(reports);
        _db.AnalysisFindingOccurrences.AddRange(reports.Select((report, index) =>
            new AnalysisFindingOccurrence
            {
                Id = 800 + index,
                AnalysisReport = report,
                AnalysisFinding = finding,
                LocationHash = $"shared-location-{index}",
                ToolName = report.ScannerName,
                ScannerKey = report.ScannerKey,
                RuleId = finding.RuleId,
                FilePath = "src/Shared.cs",
                StartLine = 10,
                Message = finding.Message,
                IsNew = index == 0,
                CreatedAt = _now
            }));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(
            1, TestContext.Current.CancellationToken);

        Assert.Single(gate.Findings);
        Assert.Equal(1, gate.FindingCount);
        Assert.Equal(1, gate.NewFindingCount);
        Assert.Equal(0, gate.DecidedFindingCount);

        // Once someone accepts it, the finding is still listed for the run but is no longer counted
        // among what is left to do, new or not.
        finding.Status = AnalysisFindingStatus.Accepted;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var decided = await new AnalysisRepository(_db).GetRunGateAsync(
            1, TestContext.Current.CancellationToken);

        Assert.Single(decided.Findings);
        Assert.Equal(0, decided.FindingCount);
        Assert.Equal(0, decided.NewFindingCount);
        Assert.Equal(1, decided.DecidedFindingCount);
    }

    [Fact]
    public async Task GetRunGateAsync_AcceptsEightDistinctDastPartitionReports()
    {
        await SeedDastPartitionEvidenceAsync(reportCount: 8);

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(
            6, AnalysisGateScopes.Security, TestContext.Current.CancellationToken);

        Assert.NotEqual(AnalysisGateStatus.Error, gate.Status);
        Assert.Empty(gate.MissingProducers);
        Assert.Equal(8, gate.Reports.Count);
    }

    [Fact]
    public async Task GetRunGateAsync_RejectsMissingDastPartitionReport()
    {
        await SeedDastPartitionEvidenceAsync(reportCount: 7);

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(
            6, AnalysisGateScopes.Security, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        Assert.Single(gate.MissingProducers, item => item.StartsWith("zap-api:task:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRunGateAsync_RejectsDuplicateDastPartitionReport()
    {
        await SeedDastPartitionEvidenceAsync(reportCount: 8);
        var duplicate = DastRequest(6008, new string('6', 64), "https://qa.example.test") with
        {
            ScannerKey = "zap-api",
            StepName = "ZAP API partition 0",
            ReportContent = """{"site":[],"duplicate":true}"""
        };
        await _service.PublishReportAsync(6, duplicate, TestContext.Current.CancellationToken);

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(
            6, AnalysisGateScopes.Security, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        Assert.Single(gate.MissingProducers, item => item.StartsWith("unexpected-report:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRunGateAsync_SeparatesQualityAndSecurityScopes()
    {
        await _service.PublishReportAsync(1, Request(), TestContext.Current.CancellationToken);
        var qualityReport = new AnalysisReport
        {
            Id = 777,
            OrganizationId = 1,
            ProjectId = 1,
            PipelineRunId = 1,
            PipelineArtifactId = 101,
            ScannerKey = "coverage",
            ScannerName = "Coverage",
            ScannerVersion = "1.0",
            Category = AnalysisCategory.Coverage,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.MetricsJson,
            ContentHash = new string('d', 64),
            CompletedAt = _now,
            CreatedAt = _now
        };
        qualityReport.Evaluation = new AnalysisEvaluation
        {
            OrganizationId = 1,
            ProjectId = 1,
            PipelineRunId = 1,
            Status = AnalysisGateStatus.Passed,
            PolicySnapshotJson = "[]",
            PolicySnapshotHash = new string('e', 64),
            EvaluatedAt = _now,
            CreatedAt = _now
        };
        _db.AnalysisReports.Add(qualityReport);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AnalysisRepository(_db);

        var quality = await repository.GetRunGateAsync(
            1, AnalysisGateScopes.Quality, TestContext.Current.CancellationToken);
        var security = await repository.GetRunGateAsync(
            1, AnalysisGateScopes.Security, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Passed, quality.Status);
        Assert.Equal(AnalysisGateStatus.Warning, security.Status);
        Assert.Equal(AnalysisCategory.Coverage, Assert.Single(quality.Reports).Category);
        Assert.Equal(AnalysisCategory.Sast, Assert.Single(security.Reports).Category);
    }

    [Fact]
    public async Task GetRunGateAsync_BlocksWhenGlobalGradeIsBelowConfiguredMinimum()
    {
        const string yaml = """
            stages:
              - name: Quality
                steps:
                  - name: Grade
                    type: analysis-gate
                    analysis_scope: quality
                    analysis_grading:
                      version: 1
                      minimum_grade: B
                      required_domains: [reliability]
                      rules:
                        - key: grade.coverage
                          domain: reliability
                          metric: coverage.line.percent
                          category: coverage
                          direction: higher-is-better
                          a: 75
                          b: 65
                          c: 55
                          d: 40
                          e: 20
            """;
        var report = new AnalysisReport
        {
            Id = 778,
            OrganizationId = 1,
            ProjectId = 1,
            PipelineRunId = 2,
            PipelineArtifactId = 102,
            ScannerKey = "coverage",
            ScannerName = "Coverage",
            ScannerVersion = "1.0",
            Category = AnalysisCategory.Coverage,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.MetricsJson,
            ContentHash = new string('f', 64),
            CommitHash = new string('b', 40),
            CompletedAt = _now,
            CreatedAt = _now,
            Metrics =
            {
                new AnalysisMetric
                {
                    OrganizationId = 1,
                    ProjectId = 1,
                    Key = "coverage.line.percent",
                    Value = 55,
                    Unit = "percent",
                    ToolName = "test",
                    CreatedAt = _now
                }
            }
        };
        var grade = AnalysisGradeEngine.Evaluate(
            report,
            new AnalysisRunContext(
                1, 1, 2, "feature", new string('b', 40), "main", PipelineYaml: yaml),
            _now);
        report.Evaluation = new AnalysisEvaluation
        {
            OrganizationId = 1,
            ProjectId = 1,
            PipelineRunId = 2,
            Status = AnalysisGateStatus.Passed,
            PolicySnapshotJson = "[]",
            PolicySnapshotHash = new string('e', 64),
            Grade = grade.Grade,
            GradeCompleteness = grade.Completeness,
            GradeSnapshotJson = grade.SnapshotJson,
            GradeSnapshotHash = grade.SnapshotHash,
            EvaluatedAt = _now,
            CreatedAt = _now
        };
        _db.AnalysisReports.Add(report);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(
            2,
            AnalysisGateScopes.Quality,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGrade.C, gate.Grade!.OverallGrade);
        Assert.Equal(AnalysisGrade.B, gate.Grade.MinimumGrade);
        Assert.Equal(AnalysisGateStatus.Blocked, gate.Status);
    }

    [Fact]
    public async Task PublishReportAsync_AcceptsExactUnexpiredDastLease()
    {
        var token = new string('a', 64);
        await SeedDastLeaseAsync(2, token, _now.AddHours(1));

        var result = await _service.PublishReportAsync(
            2,
            DastRequest(102, token, "https://qa.example.test"),
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisCategory.Dast, result.Category);
        Assert.Equal("zap-passive", result.ScannerKey);
    }

    [Fact]
    public async Task PublishReportAsync_PreservesIdenticalDastPayloadsFromDistinctSteps()
    {
        var token = new string('a', 64);
        await SeedDastLeaseAsync(2, token, _now.AddHours(1));
        var firstRequest = DastRequest(102, token, "https://qa.example.test") with
        {
            ScannerKey = "zap-api",
            StepName = "ZAP API partition 0"
        };
        var secondRequest = firstRequest with { StepName = "ZAP API partition 1" };

        var first = await _service.PublishReportAsync(
            2, firstRequest, TestContext.Current.CancellationToken);
        var second = await _service.PublishReportAsync(
            2, secondRequest, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContentHash, second.ContentHash);
        var rows = await _db.AnalysisReports
            .Where(report => report.ScannerKey == "zap-api")
            .OrderBy(report => report.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].PayloadHash, rows[1].PayloadHash);
        Assert.NotEqual(rows[0].ContentHash, rows[1].ContentHash);
    }

    [Fact]
    public async Task PublishReportAsync_RejectsExpiredDastLease()
    {
        var token = new string('b', 64);
        await SeedDastLeaseAsync(2, token, _now.AddMinutes(-1));

        await Assert.ThrowsAsync<BadRequestException>(() => _service.PublishReportAsync(
            2,
            DastRequest(102, token, "https://qa.example.test"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishReportAsync_FixesSharedFindingOnlyAfterEveryScannerStopsObservingIt()
    {
        await _service.PublishReportAsync(1, Request(), TestContext.Current.CancellationToken);
        await _service.PublishReportAsync(3, Request(3) with
        {
            ScannerKey = "second-sast",
            ScannerName = "Second SAST",
            CompletedAt = _now.AddMinutes(1)
        }, TestContext.Current.CancellationToken);

        await _service.PublishReportAsync(5, Request(5) with
        {
            ReportContent = EmptySarif(),
            CompletedAt = _now.AddMinutes(2)
        }, TestContext.Current.CancellationToken);
        Assert.Equal(AnalysisFindingStatus.Open, Assert.Single(_db.AnalysisFindings).Status);

        await _service.PublishReportAsync(6, Request(6) with
        {
            ScannerKey = "second-sast",
            ScannerName = "Second SAST",
            ReportContent = EmptySarif(),
            CompletedAt = _now.AddMinutes(3)
        }, TestContext.Current.CancellationToken);
        Assert.Equal(AnalysisFindingStatus.Fixed, Assert.Single(_db.AnalysisFindings).Status);
    }

    [Fact]
    public async Task R2_135_AnAcceptedFinding_IsFixedWhenGone_AndAcceptedAgainWhenBack()
    {
        await _service.PublishReportAsync(1, Request(1), TestContext.Current.CancellationToken);
        var finding = Assert.Single(_db.AnalysisFindings);
        await new AnalysisFindingDecisionService(new AnalysisRepository(_db), _time, Substitute.For<IAuditService>())
            .CreateFindingDecisionAsync(finding.Id, new CreateAnalysisFindingDecisionRequest
            {
                Status = AnalysisFindingStatus.Accepted,
                Reason = "Accepted while the affected component is being replaced."
            }, "security-admin", TestContext.Current.CancellationToken);

        await _service.PublishReportAsync(3, Request(3) with
        {
            ReportContent = EmptySarif(),
            StartedAt = _now.AddMinutes(1),
            CompletedAt = _now.AddMinutes(2)
        }, TestContext.Current.CancellationToken);
        Assert.Equal(AnalysisFindingStatus.Fixed, Assert.Single(_db.AnalysisFindings).Status);

        await _service.PublishReportAsync(5, Request(5) with
        {
            StartedAt = _now.AddMinutes(3),
            CompletedAt = _now.AddMinutes(4)
        }, TestContext.Current.CancellationToken);
        Assert.Equal(AnalysisFindingStatus.Accepted, Assert.Single(_db.AnalysisFindings).Status);
    }

    [Fact]
    public async Task PublishReportAsync_ReopensExpiredDecisionWhenFindingRemains()
    {
        var first = await _service.PublishReportAsync(2, Request(2), TestContext.Current.CancellationToken);
        var finding = Assert.Single(_db.AnalysisFindings);
        await new AnalysisFindingDecisionService(new AnalysisRepository(_db), _time, Substitute.For<IAuditService>())
            .CreateFindingDecisionAsync(finding.Id, new CreateAnalysisFindingDecisionRequest
            {
                Status = AnalysisFindingStatus.Accepted,
                Reason = "Accepted temporarily while the affected component is replaced.",
                ExpiresAt = _now.AddMinutes(5)
            }, "security-admin", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(10));

        await _service.PublishReportAsync(4, Request(4) with
        {
            CompletedAt = _now.AddMinutes(11),
            StartedAt = _now.AddMinutes(10)
        }, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisFindingStatus.Open, Assert.Single(_db.AnalysisFindings).Status);
        Assert.Equal(AnalysisGateStatus.Warning, (await _service.GetReportsAsync(1,
            new PaginationRequest(), TestContext.Current.CancellationToken)).Items.First(item => item.PipelineRunId == 4).GateStatus);
        Assert.Equal(AnalysisGateStatus.Warning, first.GateStatus);
    }

    [Fact]
    public async Task PublishReportAsync_ActiveScopedExceptionPreventsBlock()
    {
        await _service.CreateExceptionAsync(1, new CreateAnalysisPolicyExceptionRequest
        {
            RuleId = "cs.sql-injection",
            Reason = "Temporary exception approved for the remediation window.",
            ExpiresAt = _now.AddDays(1)
        }, "security-admin", TestContext.Current.CancellationToken);

        var report = await _service.PublishReportAsync(2, Request(2) with
        {
            StartedAt = _now.AddMinutes(1),
            CompletedAt = _now.AddMinutes(2)
        }, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Passed, report.GateStatus);
        Assert.Equal(0, report.BlockerCount);
    }

    [Fact]
    public async Task UpdatePolicyAsync_PreservesImmutableVersionSnapshots()
    {
        var created = await _service.CreatePolicyAsync(1, new UpsertAnalysisPolicyRequest
        {
            Name = "Security gate",
            Category = AnalysisCategory.Sast,
            SeverityThreshold = AnalysisSeverity.High,
            Behavior = AnalysisGateBehavior.Block
        }, TestContext.Current.CancellationToken);

        var updated = await _service.UpdatePolicyAsync(1, created.Id, new UpsertAnalysisPolicyRequest
        {
            Name = "Security gate",
            Category = AnalysisCategory.Sast,
            SeverityThreshold = AnalysisSeverity.Medium,
            Behavior = AnalysisGateBehavior.Warn
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, updated.Version);
        var revisions = _db.AnalysisPolicyRevisions.OrderBy(item => item.Version).ToList();
        Assert.Equal(2, revisions.Count);
        Assert.Contains("\"SeverityThreshold\":4", revisions[0].SnapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"SeverityThreshold\":3", revisions[1].SnapshotJson, StringComparison.Ordinal);
        Assert.NotEqual(revisions[0].SnapshotHash, revisions[1].SnapshotHash);
    }

    [Fact]
    public async Task UpdateScopedPolicyAsync_UpdatesAGlobalPolicy_AndRefusesAnotherScopeOrAProjectPolicy()
    {
        var request = new UpsertAnalysisPolicyRequest
        {
            Name = "Global gate",
            Category = AnalysisCategory.Sast,
            SeverityThreshold = AnalysisSeverity.High,
            Behavior = AnalysisGateBehavior.Block
        };
        var global = await _service.CreateScopedPolicyAsync(null, request, TestContext.Current.CancellationToken);
        var projectPolicy = await _service.CreatePolicyAsync(1, request with { Name = "Project gate" }, TestContext.Current.CancellationToken);

        var updated = await _service.UpdateScopedPolicyAsync(
            null, global.Id, request with { SeverityThreshold = AnalysisSeverity.Medium }, TestContext.Current.CancellationToken);

        Assert.Equal(2, updated.Version);
        Assert.Equal(AnalysisSeverity.Medium, updated.SeverityThreshold);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.UpdateScopedPolicyAsync(77, global.Id, request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.UpdateScopedPolicyAsync(null, projectPolicy.Id, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyPolicyBatchAsync_PersistsEveryRuleAsOneServiceOperation()
    {
        var result = await _service.ApplyPolicyBatchAsync(
            null,
            1,
            new ApplyAnalysisPolicyBatchRequest
            {
                Items =
                [
                    new AnalysisPolicyBatchItemRequest
                    {
                        Policy = new UpsertAnalysisPolicyRequest
                        {
                            PolicyKey = "quality.batch.one",
                            Name = "Batch one",
                            MetricKey = "coverage.line.percent",
                            Operator = AnalysisPolicyOperator.LessThan,
                            Threshold = 75
                        }
                    },
                    new AnalysisPolicyBatchItemRequest
                    {
                        Policy = new UpsertAnalysisPolicyRequest
                        {
                            PolicyKey = "quality.batch.two",
                            Name = "Batch two",
                            MetricKey = "coverage.branch.percent",
                            Operator = AnalysisPolicyOperator.LessThan,
                            Threshold = 70
                        }
                    }
                ]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, _db.AnalysisPolicies.Count(policy =>
            policy.PolicyKey.StartsWith("quality.batch.")));
        Assert.Equal(2, _db.AnalysisPolicyRevisions.Count(revision =>
            result.Select(policy => policy.Id).Contains(revision.AnalysisPolicyId)));
    }

    [Fact]
    public async Task RollbackPolicyAsync_CreatesNewVersionWithoutRewritingHistory()
    {
        var created = await _service.CreatePolicyAsync(1, new UpsertAnalysisPolicyRequest
        {
            PolicyKey = "quality.coverage.custom",
            Name = "Coverage",
            MetricKey = "coverage.line.percent",
            Operator = AnalysisPolicyOperator.LessThan,
            Threshold = 75,
            Behavior = AnalysisGateBehavior.Warn
        }, TestContext.Current.CancellationToken);
        await _service.UpdatePolicyAsync(1, created.Id, new UpsertAnalysisPolicyRequest
        {
            PolicyKey = "quality.coverage.custom",
            Name = "Coverage",
            MetricKey = "coverage.line.percent",
            Operator = AnalysisPolicyOperator.LessThan,
            Threshold = 85,
            Behavior = AnalysisGateBehavior.Block
        }, TestContext.Current.CancellationToken);

        var restored = await _service.RollbackPolicyAsync(
            null, 1, created.Id, 1, TestContext.Current.CancellationToken);
        var revisions = await _service.GetPolicyRevisionsAsync(
            null, 1, created.Id, TestContext.Current.CancellationToken);

        Assert.Equal(3, restored.Version);
        Assert.Equal(75, restored.Threshold);
        Assert.Equal(AnalysisGateBehavior.Warn, restored.Behavior);
        Assert.Equal([3, 2, 1], revisions.Select(item => item.Version));
        Assert.Equal(3, revisions.Select(item => item.SnapshotHash).Distinct().Count());
    }

    [Fact]
    public async Task PreviewPolicySetAsync_AppliesProjectOverrideWithoutPersistingIt()
    {
        var preview = await _service.PreviewPolicySetAsync(
            null,
            1,
            new PreviewAnalysisPolicySetRequest
            {
                Candidate = new UpsertAnalysisPolicyRequest
                {
                    PolicyKey = "quality.coverage.line",
                    Name = "Project coverage",
                    MetricKey = "coverage.line.percent",
                    Operator = AnalysisPolicyOperator.LessThan,
                    Threshold = 85,
                    Behavior = AnalysisGateBehavior.Block
                }
            },
            TestContext.Current.CancellationToken);

        var coverage = Assert.Single(
            preview.Policies,
            item => item.PolicyKey == "quality.coverage.line");
        Assert.Equal(AnalysisPolicyScope.Project, coverage.Scope);
        Assert.True(coverage.IsOverride);
        Assert.Equal(AnalysisPolicyScope.System, coverage.OverriddenScope);
        Assert.Equal(85, coverage.Threshold);
        Assert.Equal(64, preview.SnapshotHash.Length);
        Assert.Empty(_db.AnalysisPolicies);
    }

    [Fact]
    public async Task PublishReportAsync_IsIdempotentForSameRunScannerAndContent()
    {
        var request = Request();

        var first = await _service.PublishReportAsync(1, request, TestContext.Current.CancellationToken);
        var second = await _service.PublishReportAsync(1, request, TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_db.AnalysisReports);
        Assert.Single(_db.AnalysisFindings);
        Assert.Single(_db.AnalysisFindingOccurrences);
        Assert.Equal(1, first.FindingCount);
        Assert.Equal(1, first.NewFindingCount);
        Assert.Equal(AnalysisGateStatus.Warning, first.GateStatus);
        Assert.Equal(0, first.BlockerCount);
        Assert.Equal(1, first.WarningCount);
    }

    [Fact]
    public async Task PublishReportAsync_FeatureRunUsesMainBaseline()
    {
        await _service.PublishReportAsync(1, Request(), TestContext.Current.CancellationToken);

        var feature = await _service.PublishReportAsync(2, Request(2), TestContext.Current.CancellationToken);

        Assert.Equal(0, feature.NewFindingCount);
        Assert.Equal(AnalysisGateStatus.Passed, feature.GateStatus);
        Assert.Equal(2, _db.AnalysisReports.Count());
        Assert.Single(_db.AnalysisFindings);
        Assert.Equal(2, _db.AnalysisFindingOccurrences.Count());
    }

    [Fact]
    public async Task PublishReportAsync_DefaultBranchUsesLatestCompletedScannerReportAsBaseline()
    {
        var first = await _service.PublishReportAsync(3, Request(3), TestContext.Current.CancellationToken);
        var second = await _service.PublishReportAsync(
            5,
            Request(5) with { CompletedAt = _now.AddMinutes(2) },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first.NewFindingCount);
        Assert.Equal(0, second.NewFindingCount);
        Assert.Equal(PipelineStatus.Running, _db.PipelineRuns.Single(run => run.Id == 3).Status);
    }

    [Fact]
    public async Task GetProjectSummaryAsync_CountsNewFindingsFromLatestScannerReportOnly()
    {
        await _service.PublishReportAsync(3, Request(3), TestContext.Current.CancellationToken);
        await _service.PublishReportAsync(
            5,
            Request(5) with { CompletedAt = _now.AddMinutes(2) },
            TestContext.Current.CancellationToken);

        var summary = await _service.GetProjectSummaryAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(0, summary.NewCount);
        Assert.Equal(1, summary.OpenCount);
    }

    [Fact]
    public async Task GetFindingsAsync_FiltersByScannerKeyCaseInsensitively()
    {
        await _service.PublishReportAsync(1, Request(), TestContext.Current.CancellationToken);

        var result = await _service.GetFindingsAsync(
            1,
            new AnalysisFindingPaginationRequest { Scanner = "OPENGREP" },
            TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("opengrep", result.Items[0].LatestOccurrence?.ScannerKey);
    }

    [Fact]
    public async Task GetFindingsAsync_UsesIdAsStableTieBreakerAcrossPages()
    {
        _db.AnalysisFindings.AddRange(Enumerable.Range(1, 3).Select(index => new AnalysisFinding
        {
            OrganizationId = 1,
            ProjectId = 1,
            Fingerprint = $"pagination-{index}",
            RuleId = $"rule-{index}",
            Title = $"Finding {index}",
            Message = "Same timestamp",
            FirstSeenAt = _now,
            LastSeenAt = _now,
            CreatedAt = _now,
            UpdatedAt = _now
        }));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = await _service.GetFindingsAsync(
            1,
            new AnalysisFindingPaginationRequest { Page = 1, PageSize = 2 },
            TestContext.Current.CancellationToken);
        var second = await _service.GetFindingsAsync(
            1,
            new AnalysisFindingPaginationRequest { Page = 2, PageSize = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal([3, 2], first.Items.Select(item => item.Id));
        Assert.Equal([1], second.Items.Select(item => item.Id));
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));

        var idSorted = await _service.GetFindingsAsync(
            1,
            new AnalysisFindingPaginationRequest { Page = 1, PageSize = 2, SortBy = "Id" },
            TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], idSorted.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task GetFindingsAsync_NewnessUsesLatestReportOnSelectedBranch()
    {
        await _service.PublishReportAsync(3, Request(3), TestContext.Current.CancellationToken);
        await _service.PublishReportAsync(
            5,
            Request(5) with { CompletedAt = _now.AddMinutes(2) },
            TestContext.Current.CancellationToken);
        await _service.PublishReportAsync(
            4,
            Request(4) with { CompletedAt = _now.AddMinutes(3) },
            TestContext.Current.CancellationToken);
        var featureOccurrence = _db.AnalysisFindingOccurrences.OrderByDescending(item => item.Id).First();
        featureOccurrence.IsNew = true;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var mainNew = await _service.GetFindingsAsync(
            1,
            new AnalysisFindingPaginationRequest { IsNew = true },
            TestContext.Current.CancellationToken);
        var featureNew = await _service.GetFindingsAsync(
            1,
            new AnalysisFindingPaginationRequest { IsNew = true, Branch = "feature" },
            TestContext.Current.CancellationToken);

        Assert.Empty(mainNew.Items);
        Assert.Single(featureNew.Items);
    }

    [Fact]
    public async Task GetFindingsAsync_NewnessAndScannerUseTheSameLatestScannerReport()
    {
        await _service.PublishReportAsync(3, Request(3), TestContext.Current.CancellationToken);
        await _service.PublishReportAsync(
            5,
            Request(5) with
            {
                ScannerKey = "semgrep",
                ScannerName = "Semgrep",
                CompletedAt = _now.AddMinutes(2)
            },
            TestContext.Current.CancellationToken);
        await _service.PublishReportAsync(
            6,
            Request(6) with { CompletedAt = _now.AddMinutes(3) },
            TestContext.Current.CancellationToken);

        var openGrepNew = await _service.GetFindingsAsync(
            1,
            new AnalysisFindingPaginationRequest { IsNew = true, Scanner = "opengrep" },
            TestContext.Current.CancellationToken);
        var semgrepNew = await _service.GetFindingsAsync(
            1,
            new AnalysisFindingPaginationRequest { IsNew = true, Scanner = "semgrep" },
            TestContext.Current.CancellationToken);

        Assert.Empty(openGrepNew.Items);
        Assert.Single(semgrepNew.Items);
    }

    [Fact]
    public async Task GetProjectSummaryAsync_NewCountMatchesNewnessFilterAcrossStatuses()
    {
        await _service.PublishReportAsync(3, Request(3), TestContext.Current.CancellationToken);
        var finding = await _db.AnalysisFindings.SingleAsync(TestContext.Current.CancellationToken);
        finding.Status = AnalysisFindingStatus.Accepted;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var summary = await _service.GetProjectSummaryAsync(1, TestContext.Current.CancellationToken);
        var filtered = await _service.GetFindingsAsync(
            1,
            new AnalysisFindingPaginationRequest { IsNew = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(filtered.TotalCount, summary.NewCount);
        Assert.Equal(1, summary.NewCount);
    }

    [Fact]
    public async Task PublishReportAsync_RejectsArtifactFromAnotherRun()
    {
        _db.PipelineArtifacts.Add(new PipelineArtifact
        {
            Id = 7,
            PipelineId = 1,
            PipelineRunId = 2,
            Name = "report",
            FilePath = "report.sarif"
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var request = Request() with { PipelineArtifactId = 7 };

        var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
            _service.PublishReportAsync(1, request, TestContext.Current.CancellationToken));

        Assert.Contains("does not belong", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReportAsync_RejectsTruncatedSuccessfulReport()
    {
        var request = Request() with { IsTruncated = true };

        var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
            _service.PublishReportAsync(1, request, TestContext.Current.CancellationToken));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_db.AnalysisReports);
    }

    [Fact]
    public async Task PublishReportAsync_ErrorEnvelopeProducesErrorGate()
    {
        var request = Request() with
        {
            Status = AnalysisReportStatus.TimedOut,
            ReportContent = string.Empty,
            ErrorMessage = "Scanner exceeded its execution timeout."
        };

        var report = await _service.PublishReportAsync(1, request, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Error, report.GateStatus);
        Assert.Equal(1, report.BlockerCount);
        Assert.Empty(_db.AnalysisFindings);
    }

    [Fact]
    public async Task PublishReportAsync_CycloneDxPersistsComponents()
    {
        var request = Request() with
        {
            ScannerKey = "syft",
            ScannerName = "Syft",
            Category = AnalysisCategory.Sbom,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.CycloneDxJson,
            ReportContent = """
                { "bomFormat": "CycloneDX", "components": [
                  { "type": "library", "name": "YamlDotNet", "version": "16.3.0", "purl": "pkg:nuget/YamlDotNet@16.3.0" },
                  { "type": "library", "name": "YamlDotNet", "version": "16.3.0", "purl": "pkg:nuget/YamlDotNet@16.3.0" }
                ] }
                """
        };

        var report = await _service.PublishReportAsync(1, request, TestContext.Current.CancellationToken);
        var duplicate = await _service.PublishReportAsync(1, request, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ComponentCount);
        Assert.Equal(report.Id, duplicate.Id);
        Assert.Single(_db.AnalysisComponents);
        await _dependencyTrackOutbox.Received(2).EnqueueSbomAsync(
            report.Id,
            Arg.Any<CancellationToken>());
        Assert.Equal(AnalysisGateStatus.Passed, report.GateStatus);
    }

    [Fact]
    public async Task GetRunGateAsync_RequiredDependencyTrackFailsClosedUntilOutboxSucceeds()
    {
        var request = Request() with
        {
            ScannerKey = "syft",
            ScannerName = "Syft",
            Category = AnalysisCategory.Sbom,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.CycloneDxJson,
            ReportContent = """{"bomFormat":"CycloneDX","components":[]}"""
        };
        var report = await _service.PublishReportAsync(1, request, TestContext.Current.CancellationToken);
        var requiredService = CreateService(new DependencyTrackOptions { Enabled = true, Required = true });

        var pending = await requiredService.GetRunGateAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Error, pending.Status);
        Assert.Contains("dependency-track:missing", pending.MissingProducers);

        _db.DependencyTrackOutboxItems.Add(new DependencyTrackOutboxItem
        {
            AnalysisReportId = report.Id,
            OrganizationId = 1,
            ProjectId = 1,
            ExternalProjectName = "org-1/Project",
            ProjectVersion = new string('a', 40),
            ReportEntryPath = "report.sarif",
            Status = DependencyTrackOutboxStatuses.Succeeded,
            NextAttemptAt = _now,
            CompletedAt = _now,
            CreatedAt = _now,
            UpdatedAt = _now
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var succeeded = await requiredService.GetRunGateAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Passed, succeeded.Status);
        Assert.DoesNotContain(succeeded.MissingProducers, item =>
            item.StartsWith("dependency-track:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPortfolioAsync_RestrictsProjectsAndAppliesDimensions()
    {
        await _service.PublishReportAsync(1, Request(), TestContext.Current.CancellationToken);

        var denied = await _service.GetPortfolioAsync([99], new AnalysisPortfolioPaginationRequest(),
            TestContext.Current.CancellationToken);
        var visible = await _service.GetPortfolioAsync([1], new AnalysisPortfolioPaginationRequest
        {
            OrganizationId = 1,
            ProjectId = 1,
            PipelineId = 1,
            Branch = "main",
            Commit = new string('a', 12),
            Category = AnalysisCategory.Sast,
            From = _now.AddHours(-1),
            To = _now.AddHours(1)
        }, TestContext.Current.CancellationToken);

        Assert.Empty(denied.Items);
        var row = Assert.Single(visible.Items);
        Assert.Equal("Aetheus", row.OrganizationName);
        Assert.Equal("Project", row.ProjectName);
        Assert.Equal("security", row.PipelineName);
        Assert.Equal("main", row.BranchName);
        Assert.Equal(AnalysisGateStatus.Warning, row.GateStatus);
        Assert.Null(row.RepositoryId);
    }

    /// <summary>A portfolio row names the internal repository its run built (matched by the project
    /// and slug of the run's clone URL, whatever host it was re-homed to), so the commit links to
    /// its Aetheus page.</summary>
    [Fact]
    public async Task GetPortfolioAsync_ResolvesTheInternalRepositoryOfTheReportRun()
    {
        var run = await _db.PipelineRuns.SingleAsync(item => item.Id == 1, TestContext.Current.CancellationToken);
        run.RepositoryUrl = "https://host.docker.internal:5301/git/1/project.git";
        var repository = new GitInternalRepo { ProjectId = 1, Name = "Project", Slug = "project" };
        _db.GitInternalRepos.Add(repository);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await _service.PublishReportAsync(1, Request(), TestContext.Current.CancellationToken);

        var page = await _service.GetPortfolioAsync([1], new AnalysisPortfolioPaginationRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(repository.Id, Assert.Single(page.Items).RepositoryId);
    }

    [Fact]
    public async Task GetPortfolioProjectsAsync_ReturnsAccessibleProjectsWithAggregates()
    {
        await _service.PublishReportAsync(1, Request(), TestContext.Current.CancellationToken);

        var hidden = await _service.GetPortfolioProjectsAsync([99], TestContext.Current.CancellationToken);
        var visible = await _service.GetPortfolioProjectsAsync([1], TestContext.Current.CancellationToken);

        Assert.Empty(hidden);
        var project = Assert.Single(visible);
        Assert.Equal(1, project.ProjectId);
        Assert.Equal("Project", project.ProjectName);
        Assert.Equal("Aetheus", project.OrganizationName);
        Assert.Equal(1, project.ReportCount);
        Assert.NotNull(project.LastAnalysisAt);
    }

    [Fact]
    public void IsUniqueViolation_RecognizesPostgreSqlConflictOnly()
    {
        var duplicate = new DbUpdateException("duplicate", new PostgresException(
            "duplicate", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));
        var foreignKey = new DbUpdateException("foreign key", new PostgresException(
            "foreign key", "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation));

        Assert.True(AnalysisReportPublisher.IsUniqueViolation(duplicate));
        Assert.False(AnalysisReportPublisher.IsUniqueViolation(foreignKey));
    }

    private PublishAnalysisReportRequest Request(int runId = 1) => new()
    {
        ScannerKey = "opengrep",
        ScannerName = "OpenGrep",
        ScannerVersion = "1.22.0",
        Category = AnalysisCategory.Sast,
        Status = AnalysisReportStatus.Failed,
        Format = AnalysisReportFormat.Sarif,
        StartedAt = _now.AddMinutes(-1),
        CompletedAt = _now,
        ReportPath = "reports/opengrep.sarif",
        PipelineArtifactId = 100 + runId,
        RuleSetHash = new string('c', 64),
        StageName = "Security",
        StepName = "OpenGrep",
        ReportContent = """
            { "runs": [{ "tool": { "driver": { "name": "OpenGrep" } }, "results": [{
              "ruleId": "cs.sql-injection",
              "level": "error",
              "message": { "text": "Untrusted SQL input" },
              "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/Query.cs" }, "region": { "startLine": 42 } } }]
            }] }] }
            """
    };

    private AnalysisService CreateService(DependencyTrackOptions dependencyTrackOptions)
    {
        var repository = new AnalysisRepository(_db);
        return new AnalysisService(
            repository,
            new AnalysisIngestGate(Options.Create(new AnalysisPlatformOptions())),
            new AnalysisPolicyEngine(repository),
            _time,
            Substitute.For<IAuditService>(),
            Substitute.For<IDbTransactionScope>(),
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<INotificationService>(),
            _dependencyTrackOutbox,
            Options.Create(new AnalysisPlatformOptions()),
            Options.Create(dependencyTrackOptions));
    }

    private async Task SeedDastLeaseAsync(int runId, string token, DateTime expiresAt)
    {
        var environment = new Aetheus.Back.Data.Entities.Environment
        {
            Id = 42,
            Name = "qa",
            ProjectId = 1,
            Type = EnvironmentType.Testing,
            DastEnabled = true,
            DastIsEphemeral = true,
            DastAllowedHosts = "qa.example.test"
        };
        if (!await _db.Environments.AnyAsync(
                item => item.Id == environment.Id,
                TestContext.Current.CancellationToken))
            _db.Environments.Add(environment);
        _db.DastExecutionLeases.Add(new DastExecutionLease
        {
            Token = token,
            PipelineRunId = runId,
            PipelineStepRunId = 1,
            EnvironmentId = environment.Id,
            TargetHost = "qa.example.test",
            TargetPort = 443,
            CreatedAt = _now,
            ExpiresAt = expiresAt
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedDastPartitionEvidenceAsync(int reportCount)
    {
        const int runId = 6;
        var token = new string('6', 64);
        await SeedDastLeaseAsync(runId, token, _now.AddMinutes(30));
        for (var index = 0; index < 9; index++)
        {
            _db.PipelineArtifacts.Add(new PipelineArtifact
            {
                Id = 6000 + index,
                PipelineId = 1,
                PipelineRunId = runId,
                Name = $"analysis-zap-api-{index}",
                FilePath = $"analysis-zap-api-{index}.zip"
            });
            if (index < 8)
            {
                _db.Tasks.Add(new ServerTask
                {
                    Id = 6100 + index,
                    ServerId = 1,
                    PipelineRunId = runId,
                    Operation = OperationKind.PipelineRunScanner,
                    Command = "zap-api",
                    Name = $"ZAP API partition {index}"
                });
            }
        }
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        for (var index = 0; index < reportCount; index++)
        {
            var request = DastRequest(6000 + index, token, "https://qa.example.test") with
            {
                ScannerKey = "zap-api",
                StepName = $"ZAP API partition {index}"
            };
            await _service.PublishReportAsync(runId, request, TestContext.Current.CancellationToken);
        }
    }

    private PublishAnalysisReportRequest DastRequest(
        int artifactId,
        string token,
        string targetUrl) => new()
        {
            ScannerKey = "zap-passive",
            ScannerName = "OWASP ZAP",
            ScannerVersion = "2.16.1",
            Category = AnalysisCategory.Dast,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.NativeJson,
            ReportContent = """{"site":[]}""",
            ReportPath = "zap.json",
            PipelineArtifactId = artifactId,
            StageName = "DynamicSecurity",
            StepName = "ZAP passive",
            EnvironmentName = "qa",
            DastLeaseToken = token,
            DastTargetUrl = targetUrl,
            StartedAt = _now.AddMinutes(-1),
            CompletedAt = _now
        };

    private static string EmptySarif() => """
        {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"OpenGrep","rules":[]}},"results":[]}]}
        """;
}
