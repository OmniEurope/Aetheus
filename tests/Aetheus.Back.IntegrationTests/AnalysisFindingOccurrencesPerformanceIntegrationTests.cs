// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-010 lot 27: the finding page (occurrences tab) measured on a realistic volume, 200 runs whose
/// pipeline YAML is the size of the repository's own pipelines (about 60 KB), 2 000 findings and
/// 10 000 occurrences. The queries are timed and their SQL and <c>EXPLAIN (ANALYZE, BUFFERS, SERIALIZE)</c>
/// written to the test output; the guard asserts the shape (one command, no YAML column read, the
/// finding index used), never a wall-clock bound.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisFindingOccurrencesPerformanceIntegrationTests(PostgresFixture fixture)
{
    private const int RunCount = 200;
    private const int FindingCount = 2000;
    private const int FindingsPerReport = 50;
    private const int YamlBytes = 60_000;
    private const string FindingIndex = "IX_AnalysisFindingOccurrences_AnalysisFindingId_CreatedAt";

    [Fact]
    public async Task FindingPageQueries_ReadOnlyTheShownColumnsThroughTheFindingIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetAsync();
        var hotFindingId = await SeedAsync(ct);

        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(recorder)
            .Options;

        await using (var db = new AppDbContext(options))
        {
            var repository = new AnalysisRepository(db);

            var occurrences = await MeasureAsync(
                "GetFindingOccurrencesAsync(take: 100)",
                recorder,
                () => repository.GetFindingOccurrencesAsync(hotFindingId, 100, ct),
                ct);
            var finding = await MeasureAsync(
                "GetFindingAsync",
                recorder,
                () => repository.GetFindingAsync(hotFindingId, ct),
                ct);

            Assert.Equal(100, occurrences.Result.Count);
            Assert.True(occurrences.Result.Zip(occurrences.Result.Skip(1)).All(pair => pair.First.CreatedAt >= pair.Second.CreatedAt));
            Assert.All(occurrences.Result, occurrence => Assert.NotNull(occurrence.AnalysisReport.PipelineRun?.Pipeline.SourceRepositoryId));
            await AssertShapeAsync(occurrences.Commands, ct);
            Assert.NotNull(finding.Result?.LatestOccurrence?.AnalysisReport.PipelineRun?.Pipeline.SourceRepositoryId);
            var latestOccurrenceCommand = finding.Commands.Single(command =>
                command.Sql.Contains("\"AnalysisFindingOccurrences\"", StringComparison.Ordinal));
            await AssertShapeAsync([latestOccurrenceCommand], ct);
        }
    }

    private async Task AssertShapeAsync(IReadOnlyList<RecordedCommand> commands, CancellationToken ct)
    {
        var command = Assert.Single(commands);
        Assert.DoesNotContain("\"YamlSnapshot\"", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"YamlDefinition\"", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ResolvedVariablesJson\"", command.Sql, StringComparison.Ordinal);
        var plan = await ExplainAsync(command, ct);
        Assert.Contains(plan, line => line.Contains(FindingIndex, StringComparison.Ordinal));
    }

    private async Task<Measurement<T>> MeasureAsync<T>(
        string name,
        CommandRecorder recorder,
        Func<Task<T>> query,
        CancellationToken ct)
    {
        await query();
        await query();
        var timings = new List<double>();
        T result = default!;
        IReadOnlyList<RecordedCommand> commands = [];
        for (var i = 0; i < 10; i++)
        {
            recorder.Clear();
            var stopwatch = Stopwatch.StartNew();
            result = await query();
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
            commands = recorder.Commands.ToList();
        }
        timings.Sort();
        var output = new StringBuilder();
        output.AppendLine($"=== {name}: {commands.Count} command(s), median {timings[timings.Count / 2]:F1} ms, min {timings[0]:F1} ms, max {timings[^1]:F1} ms over {timings.Count} runs");
        foreach (var command in commands)
        {
            output.AppendLine("--- SQL");
            output.AppendLine(command.Sql);
            output.AppendLine("--- EXPLAIN (ANALYZE, BUFFERS, SERIALIZE)");
            foreach (var line in await ExplainAsync(command, ct)) output.AppendLine(line);
        }
        TestContext.Current.TestOutputHelper?.WriteLine(output.ToString());
        return new Measurement<T>(result, commands);
    }

    private async Task<List<string>> ExplainAsync(RecordedCommand command, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        // The text is the SQL EF Core generated for the measured query; its values travel as the
        // recorded parameters.
#pragma warning disable CA2100
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, SERIALIZE) " + command.Sql, connection);
#pragma warning restore CA2100
        foreach (var parameter in command.Parameters) explain.Parameters.Add(parameter.Clone());
        await using var reader = await explain.ExecuteReaderAsync(ct);
        var plan = new List<string>();
        while (await reader.ReadAsync(ct)) plan.Add(reader.GetString(0));
        return plan;
    }

    private async Task<int> SeedAsync(CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using var seed = new AppDbContext(options);
        await seed.Database.MigrateAsync(ct);
        seed.ChangeTracker.AutoDetectChangesEnabled = false;
        var organizationId = await seed.Organizations.OrderBy(item => item.Id).Select(item => item.Id).FirstAsync(ct);
        var project = new Project { Name = "Occurrence volume", OrganizationId = organizationId, DefaultBranch = "main" };
        seed.Projects.Add(project);
        var repository = new GitInternalRepo
        {
            Name = "occurrence-volume",
            Slug = "occurrence-volume",
            Project = project
        };
        seed.GitInternalRepos.Add(repository);
        var pipeline = new Pipeline
        {
            Name = "ci",
            Project = project,
            SourceRepository = repository,
            YamlDefinition = Yaml("definition")
        };
        seed.Pipelines.Add(pipeline);
        await seed.SaveChangesAsync(ct);

        var start = DateTime.UtcNow.AddDays(-RunCount);
        var runs = Enumerable.Range(0, RunCount).Select(index => new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Success,
            StartedAt = start.AddDays(index),
            CompletedAt = start.AddDays(index).AddMinutes(20),
            BranchName = "main",
            CommitHash = index.ToString("x40"),
            YamlSnapshot = Yaml($"run {index}"),
            ResolvedVariablesJson = "{\"BUILD\":\"" + index + "\"}"
        }).ToList();
        seed.PipelineRuns.AddRange(runs);
        var findings = Enumerable.Range(0, FindingCount).Select(index => new AnalysisFinding
        {
            OrganizationId = organizationId,
            ProjectId = project.Id,
            Fingerprint = index.ToString("x64"),
            RuleId = $"rule.{index % 40}",
            Category = AnalysisCategory.Sast,
            Severity = (AnalysisSeverity)(index % 5),
            Confidence = AnalysisConfidence.High,
            Title = $"Finding {index}",
            Message = $"Finding {index} message",
            Status = AnalysisFindingStatus.Open,
            FirstSeenAt = start,
            LastSeenAt = start.AddDays(RunCount),
            CreatedAt = start,
            UpdatedAt = start
        }).ToList();
        seed.AnalysisFindings.AddRange(findings);
        await seed.SaveChangesAsync(ct);

        for (var index = 0; index < RunCount; index++)
        {
            var at = start.AddDays(index).AddMinutes(20);
            var report = new AnalysisReport
            {
                OrganizationId = organizationId,
                ProjectId = project.Id,
                PipelineRunId = runs[index].Id,
                BranchName = "main",
                ScannerKey = "opengrep",
                ScannerName = "OpenGrep",
                ScannerVersion = "1.0",
                Category = AnalysisCategory.Sast,
                Status = AnalysisReportStatus.Passed,
                Format = AnalysisReportFormat.Sarif,
                ContentHash = index.ToString("x64"),
                StartedAt = at.AddMinutes(-5),
                CompletedAt = at,
                CreatedAt = at
            };
            // The first finding is seen by every run (the page measured); the others rotate.
            var observed = new[] { findings[0] }
                .Concat(Enumerable.Range(1, FindingsPerReport - 1)
                    .Select(offset => findings[1 + ((index * (FindingsPerReport - 1) + offset) % (FindingCount - 1))]));
            foreach (var finding in observed)
            {
                report.Occurrences.Add(new AnalysisFindingOccurrence
                {
                    AnalysisFindingId = finding.Id,
                    LocationHash = finding.Fingerprint,
                    ToolName = "OpenGrep",
                    ScannerKey = "opengrep",
                    RuleId = finding.RuleId,
                    FilePath = $"src/Module{finding.Id % 30}/File{finding.Id}.cs",
                    StartLine = 10 + finding.Id % 200,
                    EndLine = 12 + finding.Id % 200,
                    Message = finding.Message,
                    BranchName = "main",
                    CommitHash = runs[index].CommitHash,
                    IsNew = index == 0,
                    CreatedAt = at
                });
            }
            seed.AnalysisReports.Add(report);
        }
        await seed.SaveChangesAsync(ct);
        await seed.Database.ExecuteSqlRawAsync("ANALYZE", ct);
        return findings[0].Id;
    }

    /// <summary>A pipeline document the size of the repository's own (`.pipeline/aetheus-ci.yaml` is about 63 KB).</summary>
    private static string Yaml(string marker)
    {
        var builder = new StringBuilder(YamlBytes + 200);
        builder.AppendLine($"# {marker}");
        for (var step = 0; builder.Length < YamlBytes; step++)
            builder.AppendLine($"      - name: step-{step}\n        run: dotnet test tests/Project{step}/Project{step}.csproj --configuration Release --no-restore");
        return builder.ToString();
    }

    private sealed record Measurement<T>(T Result, IReadOnlyList<RecordedCommand> Commands);

    private sealed record RecordedCommand(string Sql, IReadOnlyList<NpgsqlParameter> Parameters);

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly List<RecordedCommand> _commands = [];

        public IReadOnlyList<RecordedCommand> Commands => _commands;

        public void Clear() => _commands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(new RecordedCommand(
                command.CommandText,
                command.Parameters.Cast<NpgsqlParameter>().Select(parameter => parameter.Clone()).ToList()));
            return ValueTask.FromResult(result);
        }
    }
}
