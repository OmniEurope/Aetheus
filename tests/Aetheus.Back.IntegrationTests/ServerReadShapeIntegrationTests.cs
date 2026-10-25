// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>PostgreSQL read-shape tests verify bounded rows and only the columns needed by each
/// view.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ServerReadShapeIntegrationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R480_Dashboard_ProjectsTheServerColumns_AndReadsTheShownRunGroupsWithoutYaml()
    {
        await fixture.ResetAsync();
        var seeded = await SeedAsync();
        var recorder = new CommandRecorder();
        await using var db = Measured(recorder);
        var repository = new MonitoringRepository(db);

        var servers = await repository.GetDashboardServersAsync(null, Ct);
        var serverSql = recorder.Take();
        var runs = await repository.GetRecentRunsAsync(10, null, null, Ct);
        var runSql = recorder.Take();

        Assert.Equal(seeded.ServerId, Assert.Single(servers).Id);
        Assert.DoesNotContain("SudoersBaseline", serverSql, StringComparison.Ordinal);
        Assert.DoesNotContain("HeartbeatInventoryFingerprintsJson", serverSql, StringComparison.Ordinal);
        // Ten roots out of twelve, and the child one of them triggered; the two oldest roots are not read.
        Assert.Equal(11, runs.Count);
        Assert.Contains(runs, run => run.Id == seeded.ChildRunId);
        Assert.DoesNotContain(runs, run => seeded.OldRootIds.Contains(run.Id));
        Assert.DoesNotContain("YamlSnapshot", runSql, StringComparison.Ordinal);
        Assert.DoesNotContain("YamlDefinition", runSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R481_ProjectsList_ReadsTheNewestCommitAsTopOneLookups_NotANumberingOfEveryCommit()
    {
        await fixture.ResetAsync();
        var seeded = await SeedAsync();
        var recorder = new CommandRecorder();
        await using var db = Measured(recorder);
        var repository = new ProjectRepository(db, new ProjectAnalysisGradeRepository(db));

        var insights = await repository.GetProjectListInsightsAsync([seeded.ProjectId], DateTime.UtcNow.AddMinutes(-30), Ct);

        // The commit its author dated beats the undated ones recorded before it, as
        // COALESCE(CommittedAt, CreatedAt) DESC, Id DESC ordered them.
        Assert.Equal(seeded.NewestCommitId, insights[seeded.ProjectId].LastCommitId);
        var commitSql = string.Join(" ; ", recorder.Commands.Where(command => command.Contains("\"GitCommits\"", StringComparison.Ordinal)));
        Assert.DoesNotContain("ROW_NUMBER", commitSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 1", commitSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R483_PipelineSource_ReadsFourColumns_NotTheYaml()
    {
        await fixture.ResetAsync();
        var seeded = await SeedAsync();
        var recorder = new CommandRecorder();
        await using var db = Measured(recorder);
        var repository = PipelineRepositoryOver(db);

        var fields = await repository.GetPipelineSourceFieldsAsync(seeded.PipelineId, Ct);

        Assert.Equal(new PipelineSourceFields(seeded.ProjectId, "parent", "develop", null), fields);
        var sql = Assert.Single(recorder.Commands);
        Assert.DoesNotContain("YamlDefinition", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R484_RunDetail_ReadsNoResultRows_AndTheSummariesWithoutTheCoverageFiles()
    {
        await fixture.ResetAsync();
        var seeded = await SeedAsync();
        var recorder = new CommandRecorder();
        await using var db = Measured(recorder);
        var repository = PipelineRepositoryOver(db);

        var run = await repository.GetRunDetailAsync(seeded.RootRunId, Ct);
        var detailSql = recorder.Take();
        Assert.NotNull(run);
        var results = await repository.GetRunResultSummariesAsync(run, Ct);
        var summarySql = recorder.Take();

        foreach (var table in new[] { "\"CoverageResults\"", "\"LintResults\"", "\"RunMetrics\"", "\"PipelineArtifacts\"", "\"TestResults\"" })
            Assert.DoesNotContain(table, detailSql, StringComparison.Ordinal);
        Assert.DoesNotContain("FilesJson", summarySql, StringComparison.Ordinal);
        Assert.NotNull(results.Coverage);
        Assert.Equal((100, seeded.RootRunId), (results.Coverage.LinesValid, results.Coverage.RunId));
        Assert.Empty(results.Coverage.Files);
        Assert.Equal("eslint", results.Lint?.Tool);
        Assert.Equal("loc.total", Assert.Single(results.Metrics).Key);
        Assert.Equal("drop", Assert.Single(results.Artifacts).Name);
    }

    [Fact]
    public async Task R485_RunResult_StampIsOneStatement_AndTheFindingsPageCountsTheSetOnce()
    {
        await fixture.ResetAsync();
        var seeded = await SeedAsync();
        var recorder = new CommandRecorder();
        await using var db = Measured(recorder);
        var repository = new AnalysisRunResultRepository(db);

        var stamp = await repository.GetResultStampAsync(seeded.RootRunId, Ct);
        Assert.NotNull(stamp);
        Assert.Single(recorder.Take(asList: true));
        Assert.Equal(seeded.ProjectId, stamp.ProjectId);
        Assert.True(stamp.Finished);
        Assert.NotNull(stamp.LastReportId);
        Assert.Null(await repository.GetResultStampAsync(seeded.ChildRunId + 1000, Ct));

        // Three findings over the two runs, one of them observed by both, one decided on.
        int[] runIds = [seeded.RootRunId, seeded.ChildRunId];
        var openPage = await repository.GetFindingsPageAsync(runIds, new AnalysisRunFindingsRequest { RunIds = [.. runIds] }, 1, 1, Ct);
        Assert.Equal((2, 1, 1, 2), (openPage.OpenCount, openPage.NewOpenCount, openPage.DecidedCount, openPage.TotalCount));
        var first = Assert.Single(openPage.Items);
        Assert.Equal(AnalysisSeverity.Critical, first.Severity);
        Assert.True(first.IsNew);
        var all = await repository.GetFindingsPageAsync(runIds, new AnalysisRunFindingsRequest { RunIds = [.. runIds], IncludeDecided = true }, 1, 50, Ct);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(3, all.Items.Count);

        // The Gate tab's column filters and sort translate: the decided High one, then a title sort.
        var high = await repository.GetFindingsPageAsync(runIds, new AnalysisRunFindingsRequest
        {
            RunIds = [.. runIds],
            IncludeDecided = true,
            Severities = [AnalysisSeverity.High],
            IsNew = false
        }, 1, 50, Ct);
        Assert.Equal((1, AnalysisFindingStatus.Accepted), (high.TotalCount, Assert.Single(high.Items).Status));
        var searched = await repository.GetFindingsPageAsync(runIds, new AnalysisRunFindingsRequest
        {
            RunIds = [.. runIds],
            Search = "RULE.B",
            SortBy = "Title",
            SortDescending = true
        }, 1, 50, Ct);
        Assert.Equal("rule.b", Assert.Single(searched.Items).RuleId);

        // The ID column's number filter translates too: every finding but the lowest identifier.
        var lowest = all.Items.Min(finding => finding.FindingId);
        var withoutLowest = await repository.GetFindingsPageAsync(runIds, new AnalysisRunFindingsRequest
        {
            RunIds = [.. runIds],
            IncludeDecided = true,
            IdFrom = lowest,
            IdNot = lowest
        }, 1, 50, Ct);
        Assert.Equal(2, withoutLowest.TotalCount);
        Assert.DoesNotContain(withoutLowest.Items, finding => finding.FindingId == lowest);
    }

    [Fact]
    public async Task R135_AFixedFinding_IsNeitherCountedNorListedAsDecided()
    {
        await fixture.ResetAsync();
        var seeded = await SeedAsync();
        await using (var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString).Options))
        {
            var now = DateTime.UtcNow;
            var project = await seed.Projects.SingleAsync(item => item.Id == seeded.ProjectId, Ct);
            var report = await seed.AnalysisReports.FirstAsync(item => item.PipelineRunId == seeded.RootRunId, Ct);
            var fixedFinding = new AnalysisFinding
            {
                OrganizationId = project.OrganizationId,
                ProjectId = project.Id,
                Fingerprint = new string('f', 64),
                RuleId = "rule.f",
                Category = AnalysisCategory.Sast,
                Severity = AnalysisSeverity.Medium,
                Confidence = AnalysisConfidence.High,
                Title = "Finding",
                Message = "Finding",
                Status = AnalysisFindingStatus.Fixed,
                FirstSeenAt = now,
                LastSeenAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            seed.AnalysisFindingOccurrences.Add(new AnalysisFindingOccurrence
            {
                AnalysisReportId = report.Id,
                AnalysisFinding = fixedFinding,
                LocationHash = new string('f', 64),
                ToolName = "OpenGrep",
                ScannerKey = "opengrep",
                BranchName = "main",
                RuleId = fixedFinding.RuleId,
                Message = fixedFinding.Message,
                IsNew = false,
                CreatedAt = now
            });
            await seed.SaveChangesAsync(Ct);
        }

        await using var db = Measured(new CommandRecorder());
        int[] runIds = [seeded.RootRunId, seeded.ChildRunId];
        var repository = new AnalysisRunResultRepository(db);
        // Same counts as without the fixed finding: two open, one accepted; and it is in no list.
        var page = await repository.GetFindingsPageAsync(
            runIds, new AnalysisRunFindingsRequest { RunIds = [.. runIds], IncludeDecided = true }, 1, 50, Ct);
        Assert.Equal((2, 1, 1, 3), (page.OpenCount, page.NewOpenCount, page.DecidedCount, page.TotalCount));
        Assert.DoesNotContain(page.Items, finding => finding.Status == AnalysisFindingStatus.Fixed);

        var full = await new AnalysisRepository(db).GetRunGateAsync(seeded.RootRunId, null, Ct);
        Assert.Equal((1, 1), (full.FindingCount, full.DecidedFindingCount));
        Assert.DoesNotContain(full.Findings, finding => finding.Status == AnalysisFindingStatus.Fixed);
        var summary = await new AnalysisRepository(db).GetRunResultSummaryAsync(seeded.RootRunId, 10, Ct);
        Assert.Equal((1, 1), (summary.FindingCount, summary.DecidedFindingCount));
        Assert.DoesNotContain(summary.Findings, finding => finding.Status == AnalysisFindingStatus.Fixed);
    }

    [Fact]
    public async Task R485_RunResultSummary_ListsTheMostSevereFindings_AndCountsThemAll()
    {
        await fixture.ResetAsync();
        var seeded = await SeedAsync();
        await using var db = Measured(new CommandRecorder());

        var summary = await new AnalysisRepository(db).GetRunResultSummaryAsync(seeded.RootRunId, 1, Ct);
        var full = await new AnalysisRepository(db).GetRunGateAsync(seeded.RootRunId, null, Ct);

        // The root run observed the shared Critical finding (open) and the accepted High one.
        Assert.Equal(AnalysisSeverity.Critical, Assert.Single(summary.Findings).Severity);
        Assert.Equal((1, 0, 1), (summary.FindingCount, summary.NewFindingCount, summary.DecidedFindingCount));
        Assert.Equal((full.FindingCount, full.NewFindingCount, full.DecidedFindingCount),
            (summary.FindingCount, summary.NewFindingCount, summary.DecidedFindingCount));
        Assert.Equal(2, full.Findings.Count);
    }

    private AppDbContext Measured(CommandRecorder recorder) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(recorder)
            .Options);

    private static PipelineRepository PipelineRepositoryOver(AppDbContext db) =>
        new(db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
            NSubstitute.Substitute.For<Aetheus.Back.Components.Tasks.IPipelineTaskLifecycle>(),
            NSubstitute.Substitute.For<IPipelineRunLineageReader>());

    private sealed record Seeded(
        int ProjectId, int PipelineId, int ServerId, int RootRunId, int ChildRunId,
        IReadOnlyList<int> OldRootIds, int NewestCommitId);

    private async Task<Seeded> SeedAsync()
    {
        await using var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString).Options);
        var now = DateTime.UtcNow;
        var organization = new Organization { Name = $"org-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..12] };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(Ct);
        var project = new Project { Name = "Read shapes", OrganizationId = organization.Id, DefaultBranch = "main" };
        seed.Projects.Add(project);
        seed.Servers.Add(new Server
        {
            Name = "srv",
            Hostname = "srv",
            OrganizationId = organization.Id,
            Status = ServerStatus.Online,
            SudoersBaseline = new string('x', 2000),
            HeartbeatInventoryFingerprintsJson = "{}"
        });
        await seed.SaveChangesAsync(Ct);
        // CreatedAt is stamped by the context at insert time: the undated commits are "now", so the commit
        // its author dated an hour ahead is the newest, and the two undated ones are ordered by id.
        var undatedOld = new GitCommit { ProjectId = project.Id, Sha = new string('1', 40) };
        var undatedNew = new GitCommit { ProjectId = project.Id, Sha = new string('2', 40) };
        var dated = new GitCommit { ProjectId = project.Id, Sha = new string('3', 40), CommittedAt = now.AddHours(1) };
        seed.GitCommits.AddRange(undatedOld, undatedNew, dated);

        var parent = new Pipeline { ProjectId = project.Id, Name = "parent", YamlDefinition = "name: parent\nstages: []", SourceBranch = "develop" };
        var child = new Pipeline { ProjectId = project.Id, Name = "child", YamlDefinition = "name: child\nstages: []" };
        seed.Pipelines.AddRange(parent, child);
        await seed.SaveChangesAsync(Ct);

        var roots = Enumerable.Range(0, 12)
            .Select(index => new PipelineRun
            {
                PipelineId = parent.Id,
                Status = PipelineStatus.Success,
                YamlSnapshot = "name: parent",
                StartedAt = now.AddMinutes(-10 * (index + 1)),
                CompletedAt = now.AddMinutes(-10 * (index + 1) + 5)
            })
            .ToList();
        var childRun = new PipelineRun
        {
            PipelineId = child.Id,
            Status = PipelineStatus.Success,
            StartedAt = now.AddMinutes(-9),
            CompletedAt = now.AddMinutes(-8)
        };
        seed.PipelineRuns.AddRange(roots);
        seed.PipelineRuns.Add(childRun);
        await seed.SaveChangesAsync(Ct);
        var root = roots[0];
        seed.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = root.Id,
            StageName = "orchestrate",
            StepName = "child",
            TriggeredRunId = childRun.Id,
            Status = TaskExecutionStatus.Success
        });

        seed.CoverageResults.AddRange(
            new CoverageResult { PipelineRunId = root.Id, LinesValid = 100, LinesCovered = 80, LineRate = 0.8, FilesJson = "[]", CreatedAt = now },
            new CoverageResult { PipelineRunId = root.Id, LinesValid = 10, LinesCovered = 1, LineRate = 0.1, FilesJson = "[]", CreatedAt = now });
        seed.LintResults.Add(new LintResult { PipelineRunId = root.Id, Tool = "eslint", CreatedAt = now });
        seed.RunMetrics.Add(new RunMetric { PipelineRunId = root.Id, Key = "loc.total", Value = 10, CreatedAt = now });
        seed.PipelineArtifacts.Add(new PipelineArtifact
        {
            PipelineRunId = root.Id,
            PipelineId = parent.Id,
            ProjectId = project.Id,
            Name = "drop",
            FilePath = "x/drop.zip",
            CreatedAt = now,
            RetentionExpiresAt = now.AddDays(30)
        });

        AnalysisFinding Finding(char fingerprint, AnalysisSeverity severity, AnalysisFindingStatus status) => new()
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            Fingerprint = new string(fingerprint, 64),
            RuleId = $"rule.{fingerprint}",
            Category = AnalysisCategory.Sast,
            Severity = severity,
            Confidence = AnalysisConfidence.High,
            Title = "Finding",
            Message = "Finding",
            Status = status,
            FirstSeenAt = now,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        var shared = Finding('a', AnalysisSeverity.Critical, AnalysisFindingStatus.Open);
        var onlyChild = Finding('b', AnalysisSeverity.Low, AnalysisFindingStatus.Open);
        var accepted = Finding('c', AnalysisSeverity.High, AnalysisFindingStatus.Accepted);
        AnalysisReport Report(int runId, char hash, params (AnalysisFinding Finding, bool IsNew)[] occurrences)
        {
            var report = new AnalysisReport
            {
                OrganizationId = organization.Id,
                ProjectId = project.Id,
                PipelineRunId = runId,
                BranchName = "main",
                ScannerKey = "opengrep",
                ScannerName = "OpenGrep",
                ScannerVersion = "1.0",
                Category = AnalysisCategory.Sast,
                Status = AnalysisReportStatus.Passed,
                Format = AnalysisReportFormat.Sarif,
                ContentHash = new string(hash, 64),
                StartedAt = now,
                CompletedAt = now,
                CreatedAt = now
            };
            foreach (var (finding, isNew) in occurrences)
                report.Occurrences.Add(new AnalysisFindingOccurrence
                {
                    AnalysisFinding = finding,
                    LocationHash = new string(hash, 64),
                    ToolName = "OpenGrep",
                    ScannerKey = "opengrep",
                    BranchName = "main",
                    RuleId = finding.RuleId,
                    Message = finding.Message,
                    IsNew = isNew,
                    CreatedAt = now
                });
            return report;
        }
        seed.AnalysisReports.AddRange(
            Report(root.Id, 'd', (shared, false), (accepted, false)),
            Report(childRun.Id, 'e', (shared, true), (onlyChild, false)));
        await seed.SaveChangesAsync(Ct);

        return new Seeded(project.Id, parent.Id,
            await seed.Servers.Where(server => server.OrganizationId == organization.Id).Select(server => server.Id).SingleAsync(Ct),
            root.Id, childRun.Id, [roots[10].Id, roots[11].Id], dated.Id);
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        /// <summary>The statements sent since the last call, joined, and forgotten.</summary>
        public string Take()
        {
            var text = string.Join("\n;\n", Commands);
            Commands.Clear();
            return text;
        }

        public List<string> Take(bool asList)
        {
            var commands = Commands.ToList();
            Commands.Clear();
            return commands;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
