// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Analysis;

/// <summary> The quality page links the commit its grade was measured on, the latest analysis run
/// and the latest release. The summary carries what those links need. </summary>
public sealed class AnalysisProjectSummaryLinksTests : IDisposable
{
    private const string Sha = "773f2080aabbccddeeff00112233445566778899";
    private readonly AppDbContext _db;
    private readonly DateTime _now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    public AnalysisProjectSummaryLinksTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Summary_CarriesTheGradeCommit_TheLastAnalysisRun_AndTheLatestRelease()
    {
        var project = new Project { Id = 5, Name = "Aetheus", Description = "d", DefaultBranch = "develop" };
        _db.Projects.Add(project);
        _db.GitCommits.AddRange(
            new GitCommit { Id = 40, ProjectId = 5, Sha = "0000000000000000", CreatedAt = _now },
            new GitCommit { Id = 41, ProjectId = 5, Sha = Sha, CreatedAt = _now });
        _db.Releases.AddRange(
            new Release { Id = 10, ProjectId = 5, Version = "1.4.0", PublishedAt = _now.AddDays(-2), DetectedAt = _now.AddDays(-2) },
            new Release { Id = 12, ProjectId = 5, Version = "1.4.2", PublishedAt = _now.AddDays(-1), DetectedAt = _now.AddDays(-1) },
            // Detected on a branch, never published: listed after every published release.
            new Release { Id = 13, ProjectId = 5, Version = "1.5.0-dev", DetectedAt = _now });
        _db.AnalysisEvaluations.AddRange(
            Evaluation(id: 1, runId: 2450, evaluatedAt: _now.AddHours(-3)),
            Evaluation(id: 2, runId: 2464, evaluatedAt: _now.AddHours(-1)));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var summary = await new AnalysisProjectSummaryRepository(_db).GetAsync(5, TestContext.Current.CancellationToken);

        Assert.Equal(Sha, summary.Grade?.CommitHash);
        Assert.Equal(41, summary.GradeCommitId);
        Assert.Equal(2464, summary.LastAnalysisRunId);
        Assert.Equal(12, summary.LatestReleaseId);
        Assert.Equal("1.4.2", summary.LatestReleaseVersion);
    }

    /// <summary>R-25: the latest release's short application version is the series and the release number
    /// of the run that published it ("1.5.24"), not the long candidate identifier.</summary>
    [Fact]
    public async Task Summary_CarriesTheApplicationVersionOfTheRunThatPublishedTheLatestRelease()
    {
        _db.Projects.Add(new Project { Id = 7, Name = "Versioned", Description = "d" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 300, ReleaseSeries = "1.5", ReleaseNumber = 23 },
            new PipelineRun { Id = 301, ReleaseSeries = "1.5", ReleaseNumber = 24 });
        _db.Releases.AddRange(
            new Release { Id = 20, ProjectId = 7, Version = "c-1111111111111111", PublishedAt = _now.AddDays(-2), DetectedAt = _now.AddDays(-2), CreatedByPipelineRunId = 300 },
            new Release { Id = 21, ProjectId = 7, Version = "c-4dafab96a5d19d9b957bb7", PublishedAt = _now.AddDays(-1), DetectedAt = _now.AddDays(-1), CreatedByPipelineRunId = 301, PipelineRunId = 999 });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var summary = await new AnalysisProjectSummaryRepository(_db).GetAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal("c-4dafab96a5d19d9b957bb7", summary.LatestReleaseVersion);
        Assert.Equal("1.5.24", summary.LatestReleaseApplicationVersion);
    }

    [Fact]
    public async Task Summary_WithoutHistory_LeavesTheLinksEmpty()
    {
        _db.Projects.Add(new Project { Id = 6, Name = "Empty", Description = "d" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var summary = await new AnalysisProjectSummaryRepository(_db).GetAsync(6, TestContext.Current.CancellationToken);

        Assert.Null(summary.GradeCommitId);
        Assert.Null(summary.LastAnalysisRunId);
        Assert.Null(summary.LatestReleaseId);
        Assert.Null(summary.LatestReleaseVersion);
        Assert.Null(summary.LatestReleaseApplicationVersion);
    }

    private AnalysisEvaluation Evaluation(int id, int runId, DateTime evaluatedAt)
    {
        var snapshot = new AnalysisGradeSummaryDto
        {
            EvaluatedAt = evaluatedAt,
            CommitHash = Sha,
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.Security,
                    Grade = AnalysisGrade.A,
                    Required = true,
                    Completeness = AnalysisGradeCompleteness.Complete,
                    EvaluatedAt = evaluatedAt,
                    Measures =
                    [
                        new AnalysisGradeMeasureDto
                        {
                            Key = "security",
                            Domain = AnalysisGradeDomain.Security,
                            Grade = AnalysisGrade.A,
                            Required = true,
                            Observed = true,
                            ObservedValue = 100,
                            Direction = AnalysisMetricDirection.HigherIsBetter,
                            AThreshold = 90,
                            BThreshold = 80,
                            CThreshold = 70,
                            DThreshold = 60,
                            EThreshold = 50
                        }
                    ]
                }
            ]
        };
        return new AnalysisEvaluation
        {
            Id = id,
            ProjectId = 5,
            AnalysisReportId = id,
            PipelineRunId = runId,
            Status = AnalysisGateStatus.Passed,
            Grade = AnalysisGrade.A,
            GradeCompleteness = AnalysisGradeCompleteness.Complete,
            GradeSnapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            EvaluatedAt = evaluatedAt,
            CreatedAt = evaluatedAt
        };
    }
}
