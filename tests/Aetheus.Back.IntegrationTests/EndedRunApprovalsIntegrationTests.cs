// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>The PostgreSQL closing query rejects pending approvals of ended runs while retaining
/// live-run approvals.</summary>
[Collection(PostgresCollection.Name)]
public sealed class EndedRunApprovalsIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EndedRuns_LoseTheirPendingApprovals_WaitingRunsKeepTheirs()
    {
        await fixture.ResetAsync();
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        int cancelledApproval, approvedApproval, waitingApproval;
        await using (var seed = new AppDbContext(options))
        {
            var organization = new Organization { Name = $"org-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..12] };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(ct);
            var project = new Project { Name = "Approvals", OrganizationId = organization.Id };
            seed.Projects.Add(project);
            await seed.SaveChangesAsync(ct);
            var environment = new Environment { Name = "prod", ProjectId = project.Id, RequireApproval = true };
            var pipeline = new Pipeline { ProjectId = project.Id, Name = "deploy", YamlDefinition = "name: deploy\nstages: []" };
            seed.Add(environment);
            seed.Pipelines.Add(pipeline);
            await seed.SaveChangesAsync(ct);

            var ended = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Cancelled, StartedAt = now.AddHours(-13) };
            var waiting = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.WaitingForApproval, StartedAt = now.AddMinutes(-5) };
            seed.PipelineRuns.AddRange(ended, waiting);
            await seed.SaveChangesAsync(ct);

            var approved = new PipelineApproval { PipelineRunId = ended.Id, StageName = "Restore candidate", EnvironmentId = environment.Id, Status = ApprovalStatus.Approved, RequestedAt = now.AddHours(-13) };
            var stuck = new PipelineApproval { PipelineRunId = ended.Id, StageName = "Rollback", EnvironmentId = environment.Id, Status = ApprovalStatus.Pending, RequestedAt = now.AddHours(-12) };
            var live = new PipelineApproval { PipelineRunId = waiting.Id, StageName = "Deploy", EnvironmentId = environment.Id, Status = ApprovalStatus.Pending, RequestedAt = now.AddMinutes(-4) };
            seed.PipelineApprovals.AddRange(approved, stuck, live);
            await seed.SaveChangesAsync(ct);
            (cancelledApproval, approvedApproval, waitingApproval) = (stuck.Id, approved.Id, live.Id);
        }

        await using (var db = new AppDbContext(options))
        {
            var closed = await new PipelineApprovalRepository(db)
                .CloseApprovalsOfEndedRunsAsync(null, now, EndedRunApprovals.Reason, ct);
            Assert.Equal(1, closed);
        }

        await using var read = new AppDbContext(options);
        var rows = await read.PipelineApprovals.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        Assert.Equal(ApprovalStatus.Rejected, rows[cancelledApproval].Status);
        Assert.Equal(EndedRunApprovals.Reason, rows[cancelledApproval].Comments);
        Assert.Null(rows[cancelledApproval].ResolvedByUserId);
        Assert.Equal(now, rows[cancelledApproval].ResolvedAt);
        Assert.Equal(ApprovalStatus.Approved, rows[approvedApproval].Status);
        Assert.Equal(ApprovalStatus.Pending, rows[waitingApproval].Status);
    }
}
