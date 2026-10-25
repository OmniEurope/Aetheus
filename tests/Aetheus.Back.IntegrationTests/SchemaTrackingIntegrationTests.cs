// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-18, on PostgreSQL: the schema-tracking endpoints read what a deployment recorded. The queries that
/// find the release a migration was first applied by and the last verdict of an environment (through the server
/// its migrate step ran on) are real SQL here, which the in-memory unit tests do not prove.
/// <para>
/// The release a migration was first applied by is searched across every run of the shared database, so each test
/// removes the rows it seeded: the cases would otherwise see each other's deployments.
/// </para>
/// </summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class SchemaTrackingIntegrationTests(ApiSmokeFixture fixture)
{
    // The expand migration of the AI runner profile API columns: the unmap step waits for its release.
    private const string RequiredMigration = "20261011110402_AddAiRunnerProfileApiSettings";

    [Fact]
    public async Task RunAndEnvironment_ShowTheMigrationsAndThePendingContractionsAHistoryRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await Deployment.SeedAsync(fixture, ct);
        using var client = fixture.CreateAdminClient();

        var run = await client.GetFromJsonAsync<PipelineRunSchemaDto>(
            $"/api/pipelines/runs/{seed.RunId}/schema-tracking", IntegrationJsonOptions.Default, ct);

        Assert.NotNull(run);
        Assert.Equal(seed.RunId, run.RunId);
        var migration = Assert.Single(run.Migrations);
        Assert.Equal(RequiredMigration, migration.MigrationId);
        Assert.Equal(SchemaMigrationKind.Expand, migration.Kind);
        Assert.True(migration.Applied);
        var unmap = Assert.Single(run.PendingContractions, c => c.Id == "ai-runner-profile-legacy-columns-unmap");
        Assert.Equal(seed.Version, unmap.AppliedInRelease);
        Assert.Equal(seed.NextVersion, unmap.PossibleFromRelease);

        var environment = await client.GetFromJsonAsync<EnvironmentSchemaDto>(
            $"/api/environments/{seed.EnvironmentId}/schema-tracking", IntegrationJsonOptions.Default, ct);

        Assert.NotNull(environment);
        Assert.Equal(seed.RunId, environment.LastGate!.RunId);
        Assert.Equal(MigrationGateOutcome.Compatible, environment.LastGate.Outcome);
        Assert.NotEmpty(environment.PendingContractions);
    }

    // A redeploy or a rollback records the same release again and overwrites Release.PipelineRunId with the later
    // run. The release the migration was first applied by is still the one the first run's own release step recorded.
    [Fact]
    public async Task TheReleaseAMigrationWasFirstAppliedBy_SurvivesALaterRunRecordingTheSameRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await Deployment.SeedAsync(fixture, ct);
        await seed.RedeployAsync(ct);
        using var client = fixture.CreateAdminClient();

        var run = await client.GetFromJsonAsync<PipelineRunSchemaDto>(
            $"/api/pipelines/runs/{seed.RunId}/schema-tracking", IntegrationJsonOptions.Default, ct);

        var unmap = Assert.Single(run!.PendingContractions, c => c.Id == "ai-runner-profile-legacy-columns-unmap");
        Assert.Equal(seed.Version, unmap.AppliedInRelease);
        Assert.Equal(seed.NextVersion, unmap.PossibleFromRelease);
    }

    [Fact]
    public async Task AnUnknownRunOrEnvironment_AnswersNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.CreateAdminClient();

        using var run = await client.GetAsync("/api/pipelines/runs/2147483000/schema-tracking", ct);
        using var environment = await client.GetAsync("/api/environments/2147483000/schema-tracking", ct);

        Assert.Equal(HttpStatusCode.NotFound, run.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, environment.StatusCode);
    }

    /// <summary>A deployment as the control plane records it: a run whose migrate step the gate let through, whose
    /// release step published the release, and a production environment holding the server it ran on. Disposing it
    /// deletes everything it created.</summary>
    private sealed class Deployment : IAsyncDisposable
    {
        private readonly ApiSmokeFixture _fixture;
        private readonly List<int> _runIds = [];

        private Deployment(ApiSmokeFixture fixture) => _fixture = fixture;

        public int RunId { get; private set; }
        public int ReleaseId { get; private set; }
        public int EnvironmentId { get; private set; }
        public string Version { get; private set; } = string.Empty;
        public string NextVersion { get; private set; } = string.Empty;

        public static async Task<Deployment> SeedAsync(ApiSmokeFixture fixture, CancellationToken ct)
        {
            var deployment = new Deployment(fixture);
            try
            {
                await deployment.CreateAsync(ct);
            }
            catch
            {
                await deployment.DisposeAsync();
                throw;
            }
            return deployment;
        }

        private async Task CreateAsync(CancellationToken ct)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var number = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000, 900000);
            Version = $"9.9.{number}";
            NextVersion = $"9.9.{number + 1}";

            var release = new Release { ProjectId = _fixture.ProjectId, Version = Version };
            db.Releases.Add(release);
            await db.SaveChangesAsync(ct);
            ReleaseId = release.Id;

            RunId = await AddRunAsync(db, DateTime.UtcNow, ct);
            release.PipelineRunId = RunId;
            db.PipelineMigrationGateVerdicts.Add(new PipelineMigrationGateVerdict
            {
                PipelineRunId = RunId,
                StageName = "Deploy",
                StepName = "Migrate",
                Outcome = MigrationGateOutcome.Compatible,
                PendingCount = 1,
                MigrationsJson = $"[{{\"migrationId\":\"{RequiredMigration}\",\"compatible\":true,\"violations\":[]}}]",
                CreatedAt = DateTime.UtcNow
            });
            var environment = new Aetheus.Back.Data.Entities.Environment
            {
                Name = $"schema-{Guid.NewGuid():N}",
                Type = EnvironmentType.Production,
                ProjectId = _fixture.ProjectId
            };
            db.Environments.Add(environment);
            await db.SaveChangesAsync(ct);
            EnvironmentId = environment.Id;
            db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = environment.Id, ServerId = _fixture.ServerId });
            await db.SaveChangesAsync(ct);
        }

        /// <summary>A later run records the same release again, as a redeploy does, and takes over
        /// <see cref="Release.PipelineRunId"/>.</summary>
        public async Task RedeployAsync(CancellationToken ct)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var laterRunId = await AddRunAsync(db, DateTime.UtcNow.AddMinutes(1), ct);
            var release = await db.Releases.SingleAsync(r => r.Id == ReleaseId, ct);
            release.PipelineRunId = laterRunId;
            await db.SaveChangesAsync(ct);
        }

        // The run's release step publishes the release it recorded, which is how the run is tied to it for good.
        private async Task<int> AddRunAsync(AppDbContext db, DateTime startedAt, CancellationToken ct)
        {
            var run = new PipelineRun
            {
                PipelineId = _fixture.PipelineId,
                Status = PipelineStatus.Success,
                StartedAt = startedAt,
                CompletedAt = startedAt
            };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(ct);
            _runIds.Add(run.Id);
            db.PipelineStepRuns.Add(new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = "Deploy",
                StepName = "Migrate",
                ServerId = _fixture.ServerId,
                Status = TaskExecutionStatus.Success
            });
            db.PipelineStepRuns.Add(new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = "Record release",
                StepName = "Mark the exact candidate release as deployed",
                Status = TaskExecutionStatus.Success,
                OutputVariablesJson = $"{{\"RELEASE_ID\":\"{ReleaseId}\"}}"
            });
            await db.SaveChangesAsync(ct);
            return run.Id;
        }

        public async ValueTask DisposeAsync()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ct = CancellationToken.None;
            await db.EnvironmentServers.Where(link => link.EnvironmentId == EnvironmentId).ExecuteDeleteAsync(ct);
            await db.Environments.Where(environment => environment.Id == EnvironmentId).ExecuteDeleteAsync(ct);
            await db.PipelineMigrationGateVerdicts.Where(verdict => _runIds.Contains(verdict.PipelineRunId)).ExecuteDeleteAsync(ct);
            await db.PipelineStepRuns.Where(step => _runIds.Contains(step.PipelineRunId)).ExecuteDeleteAsync(ct);
            await db.Releases.Where(release => release.Id == ReleaseId && ReleaseId != 0).ExecuteDeleteAsync(ct);
            await db.PipelineRuns.Where(run => _runIds.Contains(run.Id)).ExecuteDeleteAsync(ct);
        }
    }
}
