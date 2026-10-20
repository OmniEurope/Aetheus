// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Environment-less approvals remain stored, listed and expired on their own delay under
/// the migrated PostgreSQL schema.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PipelineApprovalScopeIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AnEnvironmentLessPipelineApproval_IsListedAndExpires()
    {
        await fixture.ResetAsync();
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        var now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        int runId, pipelineApprovalId, environmentApprovalId;
        await using (var seed = new AppDbContext(options))
        {
            var organization = new Organization { Name = $"org-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..12] };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(ct);
            var project = new Project { Name = "Approvals", OrganizationId = organization.Id };
            seed.Projects.Add(project);
            await seed.SaveChangesAsync(ct);
            var environment = new Environment { Name = "prod", ProjectId = project.Id, RequireApproval = true, ApprovalTimeoutMinutes = 1440 };
            var pipeline = new Pipeline { ProjectId = project.Id, Name = "gated", YamlDefinition = "name: gated\nstages: []" };
            seed.Add(environment);
            seed.Pipelines.Add(pipeline);
            await seed.SaveChangesAsync(ct);
            var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.WaitingForApproval, StartedAt = now.AddHours(-1) };
            seed.PipelineRuns.Add(run);
            await seed.SaveChangesAsync(ct);

            // Requested 45 minutes ago: past its own 30-minute delay, well inside the environment's day.
            var pipelineApproval = new PipelineApproval
            {
                PipelineRunId = run.Id,
                StageName = "Approve the run",
                Scope = ApprovalScope.Pipeline,
                TimeoutMinutes = 30,
                Status = ApprovalStatus.Pending,
                RequestedAt = now.AddMinutes(-45)
            };
            var environmentApproval = new PipelineApproval
            {
                PipelineRunId = run.Id,
                StageName = "Deploy",
                Scope = ApprovalScope.Environment,
                EnvironmentId = environment.Id,
                Status = ApprovalStatus.Pending,
                RequestedAt = now.AddMinutes(-45)
            };
            seed.PipelineApprovals.AddRange(pipelineApproval, environmentApproval);
            await seed.SaveChangesAsync(ct);
            (runId, pipelineApprovalId, environmentApprovalId) = (run.Id, pipelineApproval.Id, environmentApproval.Id);
        }

        await using var db = new AppDbContext(options);
        var lifecycle = new PipelineApprovalRepository(db);

        var expired = await lifecycle.GetExpiredPendingApprovalIdsAsync(now, ct);
        Assert.Equal([pipelineApprovalId], expired);

        var pending = await lifecycle.GetPendingApprovalsAsync(null, ct);
        var listed = Assert.Single(pending, a => a.ApprovalId == pipelineApprovalId);
        Assert.Equal(ApprovalScope.Pipeline, listed.Scope);
        Assert.Null(listed.EnvironmentName);
        Assert.Equal("prod", Assert.Single(pending, a => a.ApprovalId == environmentApprovalId).EnvironmentName);

        var runApprovals = await db.PipelineApprovals.AsNoTracking()
            .Where(a => a.PipelineRunId == runId)
            .Include(a => a.Environment)
            .ToListAsync(ct);
        Assert.Equal(2, runApprovals.Count);
        Assert.Null(runApprovals.Single(a => a.Id == pipelineApprovalId).Environment);
    }
}
