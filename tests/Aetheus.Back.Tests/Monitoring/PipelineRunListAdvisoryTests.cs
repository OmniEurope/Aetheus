// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Monitoring;

/// <summary>
/// The run LIST (dashboard tile, recent, active and paged runs) reports an advisory AI step and the
/// category of a failure that never reached a task the same way the run detail does. Run 1302 of the
/// local mirror showed the gap: its advisory review, failed at dispatch on a missing profile, read
/// <c>isAdvisory: false</c> with no failure code on <c>runs/recent</c> while the detail had both.
/// </summary>
public sealed class PipelineRunListAdvisoryTests : IDisposable
{
    private const string Yaml = """
        name: aetheus-candidate
        stages:
          - name: Build
            steps:
              - name: Compile
                shell: dotnet build
                continue_on_error: true
          - name: AI review
            steps:
              - name: Review the last commit
                type: ai
                profile: claude-sonnet
                advisory: true
                prompt: Review.
        """;

    private const string Reason =
        "Advisory step not run: Step 'Review the last commit': AI runner profile 'claude-sonnet' was not found in the pipeline's organization.";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<PipelineRun> SeedRunAsync(string yaml)
    {
        var pipeline = new Pipeline { Name = "aetheus-candidate", YamlDefinition = yaml };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(Ct);
        var run = new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Running,
            StartedAt = new DateTime(2026, 10, 17, 8, 16, 59, DateTimeKind.Utc),
            YamlSnapshot = yaml,
            StepRuns =
            [
                new PipelineStepRun
                {
                    StageName = "Build", StepName = "Compile", Order = 0,
                    Status = TaskExecutionStatus.Failed, ContinueOnError = true
                },
                new PipelineStepRun
                {
                    StageName = "AI review", StepName = "Review the last commit", Order = 1,
                    Status = TaskExecutionStatus.Failed, ContinueOnError = true, ExitCode = -1,
                    FailureCode = TaskFailureCodes.InfrastructureMismatch, FailureReason = Reason
                }
            ]
        };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(Ct);
        return run;
    }

    [Fact]
    public async Task RecentRuns_FlagTheAdvisoryStep_AndCarryTheFailureCategory()
    {
        await SeedRunAsync(Yaml);

        var run = Assert.Single(await new MonitoringRepository(_db).GetRecentRunsAsync(5, ct: Ct));

        var review = run.Steps.Single(step => step.StageName == "AI review");
        Assert.True(review.IsAdvisory);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, review.FailureCode);
        // The reason text stays on the masked detail path; the list never carries it unmasked.
        Assert.Null(review.FailureReason);
        Assert.False(run.Steps.Single(step => step.StageName == "Build").IsAdvisory);
    }

    [Fact]
    public async Task ApplyToList_LeavesARunWithoutAdvisoryStepsUntouched()
    {
        await SeedRunAsync(Yaml.Replace("advisory: true", "findings: false", StringComparison.Ordinal));
        var listed = await _db.PipelineRuns.AsNoTracking().Select(PipelineRunListRows.Projection).ToListAsync(Ct);

        var applied = await PipelineAdvisorySteps.ApplyToListAsync(_db, listed, Ct);

        Assert.All(applied.Single().Steps, step => Assert.False(step.IsAdvisory));
    }

    [Fact]
    public async Task ApplyToList_FlagsAMatrixLegOfTheAdvisoryStep()
    {
        var run = await SeedRunAsync(Yaml);
        var listed = new List<PipelineRunDto>
        {
            new()
            {
                Id = run.Id,
                Steps =
                [
                    new PipelineStepRunDto
                    {
                        StageName = "AI review", StepName = "Review the last commit [linux]",
                        MatrixLeg = "linux", ContinueOnError = true
                    }
                ]
            }
        };

        var applied = await PipelineAdvisorySteps.ApplyToListAsync(_db, listed, Ct);

        Assert.True(applied.Single().Steps.Single().IsAdvisory);
    }

    public void Dispose() => _db.Dispose();
}
