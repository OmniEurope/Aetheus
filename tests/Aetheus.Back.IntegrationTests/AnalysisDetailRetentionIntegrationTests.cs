// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-013 lot 2 on PostgreSQL: the per-file and per-symbol metrics and the components of a report older
/// than 30 days are deleted in batches, the project totals, the report and its grade stay; a retained
/// release (one that still has an artifact) keeps its reports' detail, by run or by commit after its run
/// was deleted; a release whose artifacts are gone does not; the newest report of a scanner and a recent
/// report keep theirs.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisDetailRetentionIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private const int OldDetailRows = 12_000; // more than two batches of 5,000

    [Fact]
    public async Task OldDetailIsPurgedInBatches_TotalsGradesAndRetainedReleasesStay()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(ct);

        AnalysisDetailPurgeResult result;
        await using (var db = NewContext())
        {
            var service = new AnalysisDetailRetentionService(
                new AnalysisDetailRetentionRepository(db),
                NullLogger<AnalysisDetailRetentionService>.Instance);
            result = await service.PurgeAsync(Now, ct);
        }

        await using var check = NewContext();
        // The old report and the expired release's report, purged together: 12,003 detail rows in three
        // statements of at most 5,000 (5,000 + 5,000 + 2,003), then their three components in one.
        Assert.Equal(OldDetailRows + 3, result.Metrics);
        Assert.Equal(2 + 1, result.Components);
        Assert.Equal(2, result.Reports);
        Assert.Equal(4, result.Batches);
        Assert.Equal(0, await DetailCountAsync(check, seed.Old, ct));
        Assert.Equal(0, await check.AnalysisComponents.CountAsync(c => c.AnalysisReportId == seed.Old, ct));
        // Its totals, the report itself and its evaluation (grade) are untouched.
        Assert.Equal(2, await check.AnalysisMetrics.CountAsync(m => m.AnalysisReportId == seed.Old, ct));
        Assert.True(await check.AnalysisReports.AnyAsync(r => r.Id == seed.Old, ct));
        Assert.Equal(AnalysisGrade.B, (await check.AnalysisEvaluations.SingleAsync(e => e.AnalysisReportId == seed.Old, ct)).Grade);

        // A release that lost its artifacts is no longer retained: its old report is purged too.
        Assert.Equal(0, await DetailCountAsync(check, seed.ExpiredRelease, ct));
        Assert.Equal(1, await check.AnalysisMetrics.CountAsync(m => m.AnalysisReportId == seed.ExpiredRelease, ct));

        // Kept: the retained release's report (by its run, and by its commit once the run was deleted),
        // the newest report of its scanner, a recent report.
        Assert.Equal(3, await DetailCountAsync(check, seed.ReleasedByRun, ct));
        Assert.Equal(1, await check.AnalysisComponents.CountAsync(c => c.AnalysisReportId == seed.ReleasedByRun, ct));
        Assert.Equal(3, await DetailCountAsync(check, seed.ReleasedByCommit, ct));
        Assert.Equal(3, await DetailCountAsync(check, seed.NewestOfScanner, ct));
        Assert.Equal(3, await DetailCountAsync(check, seed.Recent, ct));

        // A second sweep finds nothing left to do.
        await using var again = NewContext();
        var second = await new AnalysisDetailRetentionService(
            new AnalysisDetailRetentionRepository(again),
            NullLogger<AnalysisDetailRetentionService>.Instance).PurgeAsync(Now, ct);
        Assert.Equal((0, 0L, 0L), (second.Reports, second.Metrics, second.Components));
    }

    private static Task<int> DetailCountAsync(AppDbContext db, int reportId, CancellationToken ct) =>
        db.AnalysisMetrics.CountAsync(m => m.AnalysisReportId == reportId && (m.FilePath != null || m.Symbol != null), ct);

    private sealed record Seeded(int Old, int ExpiredRelease, int ReleasedByRun, int ReleasedByCommit, int NewestOfScanner, int Recent);

    private async Task<Seeded> SeedAsync(CancellationToken ct)
    {
        int orgId, projectId, pipelineId;
        await using (var db = Context(Now.AddDays(-90)))
        {
            var organization = new Organization { Name = "Retention", Slug = $"retention-{Guid.NewGuid():N}" };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(ct);
            var project = new Project { Name = "Project", OrganizationId = organization.Id, DefaultBranch = "main" };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            var pipeline = new Pipeline { Name = "Quality", ProjectId = project.Id, YamlDefinition = "name: q\ntrigger: manual\nstages: []" };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(ct);
            (orgId, projectId, pipelineId) = (organization.Id, project.Id, pipeline.Id);
        }

        var old = await ReportAsync(orgId, projectId, pipelineId, "arch", "c1", Now.AddDays(-60), OldDetailRows, components: 2, grade: AnalysisGrade.B, ct: ct);
        var released = await ReportAsync(orgId, projectId, pipelineId, "arch", "c2", Now.AddDays(-50), 3, components: 1, ct: ct);
        var byCommit = await ReportAsync(orgId, projectId, pipelineId, "arch", "c3", Now.AddDays(-45), 3, components: 0, ct: ct);
        var expired = await ReportAsync(orgId, projectId, pipelineId, "arch", "c4", Now.AddDays(-40), 3, components: 1, ct: ct);
        var newest = await ReportAsync(orgId, projectId, pipelineId, "sbom", "c5", Now.AddDays(-40), 3, components: 0, ct: ct);
        var recent = await ReportAsync(orgId, projectId, pipelineId, "arch", "c6", Now.AddDays(-5), 3, components: 0, ct: ct);

        await using (var db = Context(Now.AddDays(-40)))
        {
            // Retained, by run: the release created by the run of the report, with an artifact of that run.
            var releasedArtifact = new PipelineArtifact { PipelineId = pipelineId, PipelineRunId = released.RunId, ProjectId = projectId, Name = "app", FilePath = "app.zip", RetentionExpiresAt = Now.AddDays(30) };
            db.Releases.Add(new Release { ProjectId = projectId, Version = "1.0.0", BranchName = "main", Status = ReleaseStatus.Deployed, CreatedByPipelineRunId = released.RunId, CommitHash = Commit("c2"), Artifacts = [releasedArtifact] });
            // Retained, by commit: its artifact comes from another run (the report's run is deleted below).
            var otherRun = new PipelineRun { PipelineId = pipelineId, Status = PipelineStatus.Success, StartedAt = Now.AddDays(-45), BranchName = "main", CommitHash = Commit("c3") };
            db.PipelineRuns.Add(otherRun);
            await db.SaveChangesAsync(ct);
            db.Releases.Add(new Release
            {
                ProjectId = projectId,
                Version = "1.1.0",
                BranchName = "main",
                Status = ReleaseStatus.Superseded,
                CommitHash = Commit("c3"),
                Artifacts = [new PipelineArtifact { PipelineId = pipelineId, PipelineRunId = otherRun.Id, ProjectId = projectId, Name = "app", FilePath = "app-11.zip", RetentionExpiresAt = Now.AddDays(30) }]
            });
            // Not retained: a release of the expired report's run whose artifacts were all purged.
            db.Releases.Add(new Release { ProjectId = projectId, Version = "0.9.0", BranchName = "main", Status = ReleaseStatus.Superseded, CreatedByPipelineRunId = expired.RunId, CommitHash = Commit("c4") });
            await db.SaveChangesAsync(ct);
        }
        await using (var db = NewContext())
        {
            // The retention of pipeline runs deleted the run of the "by commit" report: the report stays, unlinked.
            await db.PipelineRuns.Where(run => run.Id == byCommit.RunId).ExecuteDeleteAsync(ct);
        }

        return new Seeded(old.ReportId, expired.ReportId, released.ReportId, byCommit.ReportId, newest.ReportId, recent.ReportId);
    }

    private async Task<(int RunId, int ReportId)> ReportAsync(
        int orgId, int projectId, int pipelineId, string scanner, string commit, DateTime at, int detailRows,
        int components, CancellationToken ct, AnalysisGrade? grade = null)
    {
        await using var db = Context(at);
        var run = new PipelineRun { PipelineId = pipelineId, Status = PipelineStatus.Success, StartedAt = at, BranchName = "main", CommitHash = Commit(commit) };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var report = new AnalysisReport
        {
            OrganizationId = orgId,
            ProjectId = projectId,
            PipelineRunId = run.Id,
            ScannerKey = scanner,
            ScannerName = scanner,
            ScannerVersion = "1",
            Category = AnalysisCategory.Architecture,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.MetricsJson,
            ContentHash = Guid.NewGuid().ToString("N"),
            BranchName = "main",
            CommitHash = Commit(commit),
            StartedAt = at,
            CompletedAt = at
        };
        db.AnalysisReports.Add(report);
        await db.SaveChangesAsync(ct);
        db.AnalysisMetrics.AddRange(Enumerable.Range(0, detailRows).Select(index => new AnalysisMetric
        {
            OrganizationId = orgId,
            ProjectId = projectId,
            AnalysisReportId = report.Id,
            Key = "architecture.dependencies",
            Value = 1,
            Scope = "namespace-edge",
            ToolName = "roslyn",
            FilePath = index % 2 == 0 ? $"src/File{index}.cs" : null,
            Symbol = index % 2 == 0 ? null : $"A{index}->B{index}"
        }));
        // The totals of the run: no file, no symbol.
        db.AnalysisMetrics.Add(new AnalysisMetric { OrganizationId = orgId, ProjectId = projectId, AnalysisReportId = report.Id, Key = "architecture.cycles", Value = 0, Scope = "project", ToolName = "roslyn" });
        if (grade is not null)
        {
            db.AnalysisMetrics.Add(new AnalysisMetric { OrganizationId = orgId, ProjectId = projectId, AnalysisReportId = report.Id, Key = "loc.total", Value = 1000, Scope = "project", ToolName = "roslyn" });
            db.AnalysisEvaluations.Add(new AnalysisEvaluation { OrganizationId = orgId, ProjectId = projectId, AnalysisReportId = report.Id, PipelineRunId = run.Id, Status = AnalysisGateStatus.Passed, Grade = grade, EvaluatedAt = at });
        }
        db.AnalysisComponents.AddRange(Enumerable.Range(0, components).Select(index => new AnalysisComponent
        {
            OrganizationId = orgId,
            ProjectId = projectId,
            AnalysisReportId = report.Id,
            Name = $"package{index}",
            Version = "1.0.0"
        }));
        await db.SaveChangesAsync(ct);
        return (run.Id, report.Id);
    }

    private static string Commit(string seed) => seed.PadRight(40, '0');

    /// <summary>A context whose inserts are stamped at <paramref name="at"/>, as they were when written.</summary>
    private AppDbContext Context(DateTime at) => new(BuildOptions(), new FixedTime(at));

    private sealed class FixedTime(DateTime at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(at);
    }
}
