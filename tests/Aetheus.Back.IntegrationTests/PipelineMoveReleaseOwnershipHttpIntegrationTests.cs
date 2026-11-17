// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// BACK-002 follow-up: the releases a pipeline published follow it when it moves to another project, like its
/// artifacts. Real HTTP (JWT, <c>PUT api/pipelines/{id}</c>, <c>GET api/releases?projectId=</c>) on real
/// PostgreSQL. A release that the move cannot carry (its version already exists in the destination) and a
/// release another pipeline published stay where they are, and the move itself still succeeds. Not covered:
/// the live-release (Deployed) collision, which the migration test exercises in SQL.
/// </summary>
public sealed class PipelineMoveReleaseOwnershipHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MovingAPipeline_MovesItsPublishedReleases_AndLeavesTheOthers()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "mvrel");
        var seeded = await SeedAsync(factory, tenant);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        Assert.Contains(seeded.Followed, await ListReleaseIdsAsync(admin, tenant.ProjectId));

        using var moved = await admin.PutAsJsonAsync($"/api/pipelines/{tenant.PipelineId}", new UpdatePipelineRequest
        {
            Name = $"{tenant.Marker}-pipeline",
            ProjectId = seeded.DestinationId,
            YamlDefinition = RbacAuditSeed.PipelineYaml
        }, IntegrationJsonOptions.Default, Ct);
        Assert.True(moved.StatusCode == HttpStatusCode.OK, $"PUT gave {moved.StatusCode}: {await moved.Content.ReadAsStringAsync(Ct)}");

        var newProject = await ListReleaseIdsAsync(admin, seeded.DestinationId);
        var oldProject = await ListReleaseIdsAsync(admin, tenant.ProjectId);
        Assert.Contains(seeded.Followed, newProject);
        Assert.DoesNotContain(seeded.Followed, oldProject);
        Assert.Contains(seeded.ForeignPipeline, oldProject);
        Assert.Contains(seeded.VersionTaken, oldProject);
        Assert.DoesNotContain(seeded.ForeignPipeline, newProject);
        Assert.DoesNotContain(seeded.VersionTaken, newProject);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Releases.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.ProjectId, Ct);
        Assert.Equal(seeded.DestinationId, stored[seeded.Followed]);
        Assert.Equal(tenant.ProjectId, stored[seeded.ForeignPipeline]);
        Assert.Equal(tenant.ProjectId, stored[seeded.VersionTaken]);
    }

    private sealed record Seeded(int DestinationId, int Followed, int ForeignPipeline, int VersionTaken);

    private static async Task<Seeded> SeedAsync(AetheusWebApplicationFactory factory, RbacAuditSeed.Tenant tenant)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var destination = new Project { Name = $"{tenant.Marker}-destination", OrganizationId = tenant.OrganizationId };
        var otherPipeline = new Pipeline
        {
            Name = $"{tenant.Marker}-deploy",
            ProjectId = tenant.ProjectId,
            YamlDefinition = RbacAuditSeed.PipelineYaml,
            CreatedByUsername = "admin"
        };
        db.AddRange(destination, otherPipeline);
        await db.SaveChangesAsync(Ct);

        var publishing = new PipelineRun { PipelineId = tenant.PipelineId, Status = PipelineStatus.Success };
        var deploying = new PipelineRun { PipelineId = otherPipeline.Id, Status = PipelineStatus.Success };
        db.PipelineRuns.AddRange(publishing, deploying);
        await db.SaveChangesAsync(Ct);

        var followed = NewRelease(tenant.ProjectId, "1.0.0", publishing.Id, publishing.Id);
        // Published by the moving pipeline, then overwritten by another pipeline's deployment: not its release.
        var foreign = NewRelease(tenant.ProjectId, "1.1.0", deploying.Id, deploying.Id);
        // Published by the moving pipeline, but the destination already holds this version.
        var versionTaken = NewRelease(tenant.ProjectId, "2.0.0", publishing.Id, publishing.Id);
        var inDestination = NewRelease(destination.Id, "2.0.0", null, null);
        db.Releases.AddRange(followed, foreign, versionTaken, inDestination);
        await db.SaveChangesAsync(Ct);
        return new Seeded(destination.Id, followed.Id, foreign.Id, versionTaken.Id);
    }

    private static Release NewRelease(int projectId, string version, int? runId, int? createdByRunId) => new()
    {
        ProjectId = projectId,
        Version = version,
        BranchName = "main",
        Status = ReleaseStatus.Published,
        DetectedAt = DateTime.UtcNow,
        PublishedAt = DateTime.UtcNow,
        PipelineRunId = runId,
        CreatedByPipelineRunId = createdByRunId
    };

    private static async Task<List<int>> ListReleaseIdsAsync(HttpClient client, int projectId)
    {
        using var response = await client.GetAsync($"/api/releases?projectId={projectId}&page=1&pageSize=100", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET gave {response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        var page = await response.Content.ReadFromJsonAsync<PaginatedResult<ReleaseDto>>(IntegrationJsonOptions.Default, Ct);
        return [.. page!.Items.Select(item => item.Id)];
    }
}
