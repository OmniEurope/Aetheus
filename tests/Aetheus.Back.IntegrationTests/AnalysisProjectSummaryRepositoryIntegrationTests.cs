// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aetheus.Back.IntegrationTests;

public sealed class AnalysisProjectSummaryRepositoryIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task GetAsync_UsesAtMostThreePostgresCommands()
    {
        await fixture.ResetAsync();
        var seedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using (var seed = new AppDbContext(seedOptions))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var organizationId = await seed.Organizations.OrderBy(item => item.Id)
                .Select(item => item.Id)
                .FirstAsync(TestContext.Current.CancellationToken);
            var project = new Project
            {
                Name = "Summary budget",
                OrganizationId = organizationId,
                DefaultBranch = "main"
            };
            seed.Projects.Add(project);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var now = DateTime.UtcNow;
            var finding = new AnalysisFinding
            {
                OrganizationId = organizationId,
                ProjectId = project.Id,
                Fingerprint = new string('a', 64),
                RuleId = "security.rule",
                Category = AnalysisCategory.Sast,
                Severity = AnalysisSeverity.Critical,
                Confidence = AnalysisConfidence.High,
                Title = "Finding",
                Message = "Finding",
                Status = AnalysisFindingStatus.Open,
                FirstSeenAt = now.AddMinutes(-2),
                LastSeenAt = now,
                CreatedAt = now.AddMinutes(-2),
                UpdatedAt = now
            };
            var olderReport = new AnalysisReport
            {
                OrganizationId = organizationId,
                ProjectId = project.Id,
                BranchName = "main",
                ScannerKey = "opengrep",
                ScannerName = "OpenGrep",
                ScannerVersion = "1.0",
                Category = AnalysisCategory.Sast,
                Status = AnalysisReportStatus.Passed,
                Format = AnalysisReportFormat.Sarif,
                ContentHash = new string('b', 64),
                StartedAt = now.AddMinutes(-3),
                CompletedAt = now.AddMinutes(-2),
                CreatedAt = now.AddMinutes(-2)
            };
            olderReport.Occurrences.Add(new AnalysisFindingOccurrence
            {
                AnalysisFinding = finding,
                LocationHash = new string('c', 64),
                ToolName = "OpenGrep",
                ScannerKey = "opengrep",
                BranchName = "main",
                RuleId = finding.RuleId,
                Message = finding.Message,
                IsNew = true,
                CreatedAt = now.AddMinutes(-2)
            });
            var latestReport = new AnalysisReport
            {
                OrganizationId = organizationId,
                ProjectId = project.Id,
                ScannerKey = "opengrep",
                ScannerName = "OpenGrep",
                ScannerVersion = "1.0",
                BranchName = "main",
                Category = AnalysisCategory.Sast,
                Status = AnalysisReportStatus.Passed,
                Format = AnalysisReportFormat.Sarif,
                ContentHash = new string('d', 64),
                StartedAt = now.AddMinutes(-1),
                CompletedAt = now,
                CreatedAt = now
            };
            latestReport.Occurrences.Add(new AnalysisFindingOccurrence
            {
                AnalysisFinding = finding,
                LocationHash = new string('c', 64),
                ToolName = "OpenGrep",
                ScannerKey = "opengrep",
                BranchName = "main",
                RuleId = finding.RuleId,
                Message = finding.Message,
                IsNew = false,
                CreatedAt = now
            });
            seed.AnalysisReports.AddRange(olderReport, latestReport);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            var counter = new CommandCounter();
            var measuredOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(counter)
                .Options;
            await using var measured = new AppDbContext(measuredOptions);

            var summary = await new AnalysisProjectSummaryRepository(measured).GetAsync(
                project.Id, TestContext.Current.CancellationToken);

            Assert.Equal(1, summary.OpenCount);
            Assert.Equal(1, summary.CriticalCount);
            Assert.Equal(0, summary.NewCount);
            Assert.NotNull(summary.LastAnalysisAt);
            Assert.InRange(counter.Count, 1, 3);

            var repository = new AnalysisRepository(measured);
            var currentNew = await repository.GetFindingsAsync(
                project.Id,
                new AnalysisFindingPaginationRequest { IsNew = true },
                TestContext.Current.CancellationToken);
            var currentExisting = await repository.GetFindingsAsync(
                project.Id,
                new AnalysisFindingPaginationRequest { IsNew = false },
                TestContext.Current.CancellationToken);

            Assert.Empty(currentNew.Items);
            Assert.Single(currentExisting.Items);
        }
    }

    [Fact]
    public async Task GetGradesAsync_ReadsTheDeployedReleaseAndTheOpenFindingsInOneCommand()
    {
        await fixture.ResetAsync();
        var seedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        int graded;
        int ungraded;
        await using (var seed = new AppDbContext(seedOptions))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var organizationId = await seed.Organizations.OrderBy(item => item.Id)
                .Select(item => item.Id)
                .FirstAsync(TestContext.Current.CancellationToken);
            var now = DateTime.UtcNow;
            var gradedProject = new Project { Name = "Graded", OrganizationId = organizationId };
            var ungradedProject = new Project { Name = "Ungraded", OrganizationId = organizationId };
            seed.Projects.AddRange(gradedProject, ungradedProject);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            graded = gradedProject.Id;
            ungraded = ungradedProject.Id;

            Release NewRelease(string version, ReleaseStatus status, DateTime publishedAt, AnalysisGrade grade) => new()
            {
                ProjectId = graded,
                Version = version,
                BranchName = "main",
                Status = status,
                DetectedAt = publishedAt,
                PublishedAt = publishedAt,
                AssuranceGrade = grade,
                Deployable = true
            };
            seed.Releases.AddRange(
                NewRelease("rolled-back", ReleaseStatus.RolledBack, now.AddDays(-3), AnalysisGrade.C),
                NewRelease("deployed", ReleaseStatus.Deployed, now.AddDays(-1), AnalysisGrade.A),
                NewRelease("not-deployed", ReleaseStatus.Published, now, AnalysisGrade.F));

            AnalysisFinding NewFinding(string fingerprint, AnalysisSeverity severity, AnalysisFindingStatus status) => new()
            {
                OrganizationId = organizationId,
                ProjectId = graded,
                Fingerprint = fingerprint.PadRight(64, '0'),
                RuleId = "rule",
                Category = AnalysisCategory.Sast,
                Severity = severity,
                Confidence = AnalysisConfidence.High,
                Title = fingerprint,
                Message = fingerprint,
                Status = status,
                FirstSeenAt = now,
                LastSeenAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            seed.AnalysisFindings.AddRange(
                NewFinding("a1", AnalysisSeverity.Critical, AnalysisFindingStatus.Open),
                NewFinding("a2", AnalysisSeverity.High, AnalysisFindingStatus.Open),
                NewFinding("a3", AnalysisSeverity.High, AnalysisFindingStatus.Open),
                NewFinding("a4", AnalysisSeverity.High, AnalysisFindingStatus.Accepted),
                NewFinding("a5", AnalysisSeverity.Low, AnalysisFindingStatus.Fixed),
                NewFinding("a6", AnalysisSeverity.Info, AnalysisFindingStatus.Open),
                NewFinding("a7", AnalysisSeverity.None, AnalysisFindingStatus.Open));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var counter = new CommandCounter();
        var measuredOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options;
        await using var measured = new AppDbContext(measuredOptions);

        var grades = await new AnalysisProjectSummaryRepository(measured).GetGradesAsync(
            [graded, ungraded], TestContext.Current.CancellationToken);

        var grade = Assert.Single(grades).Value;
        Assert.Equal(graded, Assert.Single(grades).Key);
        Assert.Equal("deployed", grade.Qualification!.ReleaseVersion);
        var counts = grade.Findings!.Counts;
        Assert.Equal(1, counts.Critical);
        Assert.Equal(2, counts.High);
        Assert.Equal(0, counts.Medium);
        Assert.Equal(0, counts.Low);
        Assert.Equal(2, counts.Info);
        // The deployed release and the open findings share one command; the analysis evaluations are the other.
        Assert.InRange(counter.Count, 1, 2);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
