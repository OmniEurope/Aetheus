// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// BACK-002 (user decision): a pipeline's artifacts follow it when it moves to another project. Real HTTP
/// (JWT, <c>PUT api/pipelines/{id}</c>, <c>GET api/artifacts/project/{id}</c>) on real PostgreSQL: after the
/// move, a user who can read only the old project no longer lists the artifact, the new project lists it,
/// and the stored ProjectId that quota and retention read names the new project. Not covered: the artifact
/// file itself (no bytes are stored here; the download path has its own test).
/// </summary>
public sealed class PipelineMoveArtifactOwnershipHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MovingAPipeline_MovesItsArtifactsOutOfTheOldProjectGrid_AndIntoTheNewOne()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "move");
        var (newProjectId, artifactId) = await SeedArtifactAndSecondProjectAsync(factory, tenant);
        var oldProjectReader = await IntegrationUsers.CreateAsync(
            factory.Services, "old-project-reader",
            [new IntegrationUsers.Grant(ResourceType.Project, tenant.ProjectId, Permission.Read)],
            tenant.OrganizationId);
        using var reader = await IntegrationUsers.LoginClientAsync(factory, oldProjectReader.Username);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        Assert.Equal([artifactId], await ListArtifactIdsAsync(reader, tenant.ProjectId));

        using var moved = await admin.PutAsJsonAsync($"/api/pipelines/{tenant.PipelineId}", new UpdatePipelineRequest
        {
            Name = $"{tenant.Marker}-pipeline",
            ProjectId = newProjectId,
            YamlDefinition = RbacAuditSeed.PipelineYaml
        }, IntegrationJsonOptions.Default, Ct);
        Assert.True(moved.StatusCode == HttpStatusCode.OK, $"PUT gave {moved.StatusCode}: {await moved.Content.ReadAsStringAsync(Ct)}");

        Assert.Empty(await ListArtifactIdsAsync(reader, tenant.ProjectId));
        Assert.Empty(await ListArtifactIdsAsync(admin, tenant.ProjectId));
        Assert.Equal([artifactId], await ListArtifactIdsAsync(admin, newProjectId));

        // Quota (GetProjectTotalSizeBytesAsync) and retention (GetByPipelineAndProjectAsync) read this column.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.PipelineArtifacts.AsNoTracking().SingleAsync(a => a.Id == artifactId, Ct);
        Assert.Equal(newProjectId, stored.ProjectId);
        Assert.False(await db.PipelineArtifacts.AnyAsync(a => a.ProjectId == tenant.ProjectId, Ct));
    }

    private static async Task<(int ProjectId, int ArtifactId)> SeedArtifactAndSecondProjectAsync(
        AetheusWebApplicationFactory factory, RbacAuditSeed.Tenant tenant)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var destination = new Project { Name = $"{tenant.Marker}-destination", OrganizationId = tenant.OrganizationId };
        db.Projects.Add(destination);
        var run = new PipelineRun { PipelineId = tenant.PipelineId, Status = PipelineStatus.Success };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(Ct);

        var artifact = new PipelineArtifact
        {
            PipelineRunId = run.Id,
            PipelineId = tenant.PipelineId,
            ProjectId = tenant.ProjectId,
            Name = "drop",
            FilePath = $"artifacts/{tenant.Marker}/drop.zip",
            SizeBytes = 42,
            CreatedAt = DateTime.UtcNow,
            RetentionExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        db.PipelineArtifacts.Add(artifact);
        await db.SaveChangesAsync(Ct);
        return (destination.Id, artifact.Id);
    }

    private static async Task<List<int>> ListArtifactIdsAsync(HttpClient client, int projectId)
    {
        using var response = await client.GetAsync($"/api/artifacts/project/{projectId}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET gave {response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        var page = await response.Content.ReadFromJsonAsync<PaginatedResult<PipelineArtifactDto>>(IntegrationJsonOptions.Default, Ct);
        return [.. page!.Items.Select(item => item.Id)];
    }
}
