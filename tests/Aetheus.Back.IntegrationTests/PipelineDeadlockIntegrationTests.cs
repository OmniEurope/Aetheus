// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// End-to-end (real Npgsql + real DI graph + real EF migrations on the Postgres container)
/// regression for the "run stuck in Running forever" class of bug.
/// <para>
/// Stage A ends with a failed step flagged <c>continue_on_error</c>, which the continue-on-error
/// contract lets the run continue past: stage B becomes ready. B pins <c>agent: x</c> and no
/// pipeline runner is registered at all, so B cannot be dispatched. The old code returned silently
/// from <c>CreateTasksForNextStageAsync</c> in such a case, leaving the run <c>Running</c> with no
/// message - invisible to the mocked unit suite because it never touches a real DbContext. This
/// drives the real <see cref="IPipelineRunService"/> against real PostgreSQL and asserts the run
/// ends <c>Failed</c> with the dispatch refusal ("no online pipeline runner") persisted as its
/// reason. It is not a dependency deadlock: a continue-on-error failure does not block B.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PipelineDeadlockIntegrationTests(PostgresFixture fixture)
{
    private const string DeadlockYaml = """
        name: deadlock-it
        trigger: manual
        stages:
          - name: A
            agent: x
            steps:
              - name: s1
                shell: echo a
          - name: B
            agent: x
            depends_on: [A]
            steps:
              - name: s2
                shell: echo b
        """;

    [Fact]
    public async Task AdvanceStage_WhenTheReadyStageHasNoOnlineRunner_FailsRunWithReason()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);

        int runId;
        int blockedStepId;

        // Seed the state directly: stage A's only step Failed+ContinueOnError (so A lets the run
        // continue), stage B still Pending and depends_on A, and no runner exists to take B.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pipeline = new Pipeline { Name = $"dl-{Guid.NewGuid():N}", YamlDefinition = DeadlockYaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var run = new PipelineRun
            {
                PipelineId = pipeline.Id,
                Status = PipelineStatus.Running,
                StartedAt = TimeProvider.System.GetUtcNow().UtcDateTime
            };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            db.PipelineStepRuns.AddRange(
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "System:Prepare",
                    StepName = "prepare",
                    Order = 0,
                    Status = TaskExecutionStatus.Success,
                    IsSystem = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "A",
                    StepName = "s1",
                    Order = 1,
                    Status = TaskExecutionStatus.Failed,
                    ContinueOnError = true
                },
                new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = "B",
                    StepName = "s2",
                    Order = 2,
                    Status = TaskExecutionStatus.Pending
                });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            runId = run.Id;
            blockedStepId = await db.PipelineStepRuns
                .Where(s => s.PipelineRunId == run.Id && s.StageName == "B")
                .Select(s => s.Id)
                .FirstAsync(cancellationToken: TestContext.Current.CancellationToken);

            // Real service + real repository + real DbContext (same scope).
            var service = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
            await service.AdvanceStageAsync(run.Id, "A", ct: TestContext.Current.CancellationToken);
        }

        // Assert against a fresh context - proves the state was committed to PostgreSQL,
        // not just mutated in memory.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var run = await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(PipelineStatus.Failed, run.Status);
            Assert.NotNull(run.CompletedAt);

            Assert.NotNull(run.WarningsJson);
            var warnings = JsonSerializer.Deserialize<List<string>>(run.WarningsJson!);
            Assert.NotNull(warnings);
            Assert.Contains(warnings, w => w.Contains("Stage 'B'") && w.Contains("no online pipeline runner"));

            var blockedStep = await db.PipelineStepRuns.AsNoTracking().FirstAsync(s => s.Id == blockedStepId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(TaskExecutionStatus.Failed, blockedStep.Status);
        }
    }
}
