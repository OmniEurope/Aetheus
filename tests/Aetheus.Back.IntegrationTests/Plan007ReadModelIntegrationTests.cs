// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Verifies PostgreSQL translation for recent-run grades, redeployment targets and
/// cross-pipeline pending approvals.</summary>
[Collection(PostgresCollection.Name)]
public sealed class Plan007ReadModelIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task ReadModels_TranslateAndAnswerOnPostgres()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int projectId, deployPipelineId, deployRunId, previousReleaseId, candidateRunId;
        await using (var db = NewContext())
        {
            var organization = new Organization { Name = "Plan007", Slug = $"plan007-{Guid.NewGuid():N}" };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(ct);
            var project = new Project { Name = "Aetheus", OrganizationId = organization.Id };
            db.Projects.Add(project);
            var environment = new Aetheus.Back.Data.Entities.Environment { Name = "prod" };
            db.Environments.Add(environment);
            await db.SaveChangesAsync(ct);
            projectId = project.Id;

            var candidate = new Pipeline { Name = "candidate", ProjectId = projectId, YamlDefinition = "name: candidate" };
            var deploy = new Pipeline { Name = "deploy-prod", ProjectId = projectId, YamlDefinition = "name: deploy-prod" };
            db.Pipelines.AddRange(candidate, deploy);
            await db.SaveChangesAsync(ct);
            deployPipelineId = deploy.Id;

            var candidateRun = new PipelineRun
            {
                PipelineId = candidate.Id,
                Status = PipelineStatus.Success,
                StartedAt = DateTime.UtcNow.AddHours(-2)
            };
            var deployRun = new PipelineRun
            {
                PipelineId = deploy.Id,
                Status = PipelineStatus.WaitingForApproval,
                StartedAt = DateTime.UtcNow.AddHours(-1),
                ParametersJson = "{\"candidateVersion\":\"c-2\"}"
            };
            db.PipelineRuns.AddRange(candidateRun, deployRun);
            await db.SaveChangesAsync(ct);
            candidateRunId = candidateRun.Id;
            deployRunId = deployRun.Id;
            db.PipelineStepRuns.Add(new PipelineStepRun
            {
                PipelineRunId = candidateRun.Id,
                StageName = "seal",
                StepName = "seal",
                Status = TaskExecutionStatus.Success,
                OutputVariablesJson = "{\"CANDIDATE_ASSURANCE_GRADE\":\"B\"}"
            });
            var previous = new Release
            {
                ProjectId = projectId,
                Version = "c-1",
                Status = ReleaseStatus.Superseded,
                PublishedAt = DateTime.UtcNow.AddDays(-2),
                AssuranceGrade = AnalysisGrade.C
            };
            db.Releases.AddRange(
                previous,
                new Release
                {
                    ProjectId = projectId,
                    Version = "c-2",
                    Status = ReleaseStatus.Deployed,
                    PublishedAt = DateTime.UtcNow.AddDays(-1),
                    PipelineRunId = deployRun.Id,
                    AssuranceGrade = AnalysisGrade.B
                });
            db.PipelineApprovals.Add(new PipelineApproval
            {
                PipelineRunId = deployRun.Id,
                EnvironmentId = environment.Id,
                StageName = "Restore candidate",
                Status = ApprovalStatus.Pending,
                RequestedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            previousReleaseId = previous.Id;
        }

        await using var read = NewContext();
        var target = (await new ReleaseRepository(read).GetRedeployTargetsAsync([projectId], ct))[projectId];
        Assert.Equal(new ReleaseRedeployTarget(previousReleaseId, deployPipelineId), target);

        var pipelines = new PipelineRepository(
            read, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(read, TimeProvider.System), new PipelineRunLineageRepository(read));
        var graph = await pipelines.GetPipelinesForDependencyGraphAsync(projectId: projectId, ct: ct);
        // The candidate's own sealed letter, and the deploy's grade through the release it deploys.
        Assert.Equal(AnalysisGrade.B, graph.Single(p => p.Name == "candidate").RecentRuns.Single(r => r.Id == candidateRunId).GateGrade);
        Assert.Equal(AnalysisGrade.B, graph.Single(p => p.Name == "deploy-prod").RecentRuns.Single(r => r.Id == deployRunId).GateGrade);

        var pending = Assert.Single(await pipelines.GetPendingApprovalsAsync([deployPipelineId], ct));
        Assert.Equal(deployRunId, pending.PipelineRunId);
        Assert.Equal("prod", pending.EnvironmentName);
    }
}
