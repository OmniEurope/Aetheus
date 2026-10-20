// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// On PostgreSQL, where the Dependency-Track outbox foreign key is ON DELETE RESTRICT: an expired SBOM
/// artifact whose delivery finished is removed with its file, the delivery history staying without
/// the link; a delivery still running refuses the row delete, and the file is then left in place.
/// The InMemory provider enforces neither, which is why these live here.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ArtifactCleanupForeignKeyIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture), IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"aetheus-artifacts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true);
    }

    [Fact]
    public async Task ExpiredSbomWithAFinishedDelivery_IsRemovedWithItsFile_TheDeliveryHistoryStays()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (artifactId, deliveryId, filePath) = await SeedAsync(completedAt: Now.AddDays(-20), ct);

        await BuildCleanup().CleanupExpiredArtifactsAsync(ct);

        await using var db = NewContext();
        Assert.False(await db.PipelineArtifacts.AnyAsync(artifact => artifact.Id == artifactId, ct));
        var delivery = await db.DependencyTrackOutboxItems.SingleAsync(item => item.Id == deliveryId, ct);
        Assert.Null(delivery.PipelineArtifactId);
        Assert.False(File.Exists(Path.Combine(_storageRoot, filePath)));
    }

    [Fact]
    public async Task DeliveryStillRunning_RefusesTheRowDelete_AndTheFileIsKept()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (artifactId, deliveryId, filePath) = await SeedAsync(completedAt: null, ct);
        await using (var db = NewContext())
        {
            var artifact = await db.PipelineArtifacts.SingleAsync(candidate => candidate.Id == artifactId, ct);

            // The race the selection cannot see: a delivery enqueued after GetExpiredAsync ran.
            await Assert.ThrowsAnyAsync<Exception>(() => ArtifactPayloadRemoval.RemoveRowThenFileAsync(
                new ArtifactRepository(db), Storage(), artifact, NullLogger.Instance, ct));
        }

        await using var verification = NewContext();
        Assert.True(await verification.PipelineArtifacts.AnyAsync(artifact => artifact.Id == artifactId, ct));
        var delivery = await verification.DependencyTrackOutboxItems.SingleAsync(item => item.Id == deliveryId, ct);
        Assert.Equal(artifactId, delivery.PipelineArtifactId);
        Assert.True(File.Exists(Path.Combine(_storageRoot, filePath)));
    }

    private ArtifactStorageService Storage() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ArtifactStorage:BasePath"] = _storageRoot })
        .Build());

    private ArtifactCleanupService BuildCleanup()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddScoped<IArtifactRepository, ArtifactRepository>();
        services.AddSingleton<IArtifactStorageService>(Storage());
        var lease = Substitute.For<IPostgresLeaderLease>();
        lease.RunSerializedAsync(Arg.Any<string>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.ArgAt<CancellationToken>(2)));
        return new ArtifactCleanupService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ArtifactCleanupService>.Instance, new FixedClock(Now), lease,
            Substitute.For<IChunkedArtifactUploadService>());
    }

    private async Task<(int ArtifactId, int DeliveryId, string FilePath)> SeedAsync(DateTime? completedAt, CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = "Sbom", Slug = $"sbom-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = $"sbom-{Guid.NewGuid():N}", OrganizationId = organization.Id };
        var pipeline = new Pipeline { Name = $"sbom-{Guid.NewGuid():N}" };
        db.Projects.Add(project);
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var filePath = Path.Combine(project.Id.ToString(), pipeline.Id.ToString(), run.Id.ToString(), "sbom.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_storageRoot, filePath))!);
        await File.WriteAllBytesAsync(Path.Combine(_storageRoot, filePath), [1, 2, 3], ct);
        var artifact = new PipelineArtifact
        {
            PipelineId = pipeline.Id,
            ProjectId = project.Id,
            PipelineRunId = run.Id,
            Name = "sbom",
            FilePath = filePath,
            SizeBytes = 3,
            CreatedAt = Now.AddDays(-40),
            RetentionExpiresAt = Now.AddDays(-1)
        };
        db.PipelineArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        var report = new AnalysisReport
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            PipelineRunId = run.Id,
            PipelineArtifactId = artifact.Id,
            ScannerKey = "cyclonedx",
            ScannerName = "cyclonedx",
            ScannerVersion = "1",
            Category = AnalysisCategory.Architecture,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.MetricsJson,
            ContentHash = Guid.NewGuid().ToString("N"),
            StartedAt = Now.AddDays(-40),
            CompletedAt = Now.AddDays(-40)
        };
        db.AnalysisReports.Add(report);
        await db.SaveChangesAsync(ct);
        var delivery = new DependencyTrackOutboxItem
        {
            AnalysisReportId = report.Id,
            PipelineArtifactId = artifact.Id,
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            ExternalProjectName = "sbom",
            ProjectVersion = "1.0.0",
            ReportEntryPath = "bom.json",
            Status = completedAt is null ? DependencyTrackOutboxStatuses.Pending : DependencyTrackOutboxStatuses.Succeeded,
            NextAttemptAt = Now,
            CompletedAt = completedAt,
            CreatedAt = Now.AddDays(-40),
            UpdatedAt = Now.AddDays(-20)
        };
        db.DependencyTrackOutboxItems.Add(delivery);
        await db.SaveChangesAsync(ct);
        return (artifact.Id, delivery.Id, filePath);
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
