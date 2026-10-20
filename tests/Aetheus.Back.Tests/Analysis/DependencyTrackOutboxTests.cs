// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

public sealed class DependencyTrackOutboxTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
    private const string Sbom = """{"bomFormat":"CycloneDX","components":[]}""";

    [Fact]
    public async Task EnqueueSbomAsync_IsIdempotentAndSnapshotsImmutableReference()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        await using var provider = CreateProvider(time, Options());
        var reportId = await SeedReportAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDependencyTrackOutbox>();
            await outbox.EnqueueSbomAsync(reportId, TestContext.Current.CancellationToken);
            await outbox.EnqueueSbomAsync(reportId, TestContext.Current.CancellationToken);
        }

        await using var verification = provider.CreateAsyncScope();
        var item = Assert.Single(await verification.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .DependencyTrackOutboxItems
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("org-7/Project", item.ExternalProjectName);
        Assert.Equal(new string('a', 40), item.ProjectVersion);
        Assert.Equal("sbom.json", item.ReportEntryPath);
        Assert.Equal(DependencyTrackOutboxStatuses.Pending, item.Status);
    }

    [Fact]
    public async Task Worker_SucceedsFromVerifiedArtifactAndReleasesRetentionPin()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        var processor = Substitute.For<IDependencyTrackSubmissionProcessor>();
        await using var provider = CreateProvider(time, Options(), processor);
        var reportId = await SeedReportAndOutboxAsync(provider);
        var worker = Worker(provider, time, Options());

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        await processor.Received(1).ProcessSbomAsync(
            Arg.Is<AnalysisReportDtoContext>(context =>
                context.ReportId == reportId && context.ExternalProjectName == "org-7/Project"),
            Sbom,
            Arg.Any<CancellationToken>());
        await using var verification = provider.CreateAsyncScope();
        var item = await verification.ServiceProvider.GetRequiredService<AppDbContext>()
            .DependencyTrackOutboxItems.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DependencyTrackOutboxStatuses.Succeeded, item.Status);
        Assert.Equal(1, item.AttemptCount);
        Assert.NotNull(item.CompletedAt);
        Assert.Null(item.PipelineArtifactId);
    }

    [Fact]
    public async Task Worker_RetriesThenRaisesOneTerminalAlert()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        var processor = Substitute.For<IDependencyTrackSubmissionProcessor>();
        processor.ProcessSbomAsync(
                Arg.Any<AnalysisReportDtoContext>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("provider unavailable"));
        var options = Options(maxAttempts: 2, retryBaseSeconds: 1);
        await using var provider = CreateProvider(time, options, processor);
        await SeedReportAndOutboxAsync(provider);
        var worker = Worker(provider, time, options);

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);
        await using (var firstVerification = provider.CreateAsyncScope())
        {
            var first = await firstVerification.ServiceProvider.GetRequiredService<AppDbContext>()
                .DependencyTrackOutboxItems.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(DependencyTrackOutboxStatuses.Retrying, first.Status);
            Assert.Equal(1, first.AttemptCount);
            Assert.Null(first.CompletedAt);
            Assert.NotNull(first.PipelineArtifactId);
        }

        time.Advance(TimeSpan.FromSeconds(2));
        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        await using var verification = provider.CreateAsyncScope();
        var item = await verification.ServiceProvider.GetRequiredService<AppDbContext>()
            .DependencyTrackOutboxItems.SingleAsync(TestContext.Current.CancellationToken);
        var tracking = await verification.ServiceProvider.GetRequiredService<AppDbContext>()
            .AnalysisTrackingProjects.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DependencyTrackOutboxStatuses.Failed, item.Status);
        Assert.Equal(2, item.AttemptCount);
        Assert.NotNull(item.CompletedAt);
        Assert.Null(item.PipelineArtifactId);
        Assert.Equal("Failed", tracking.SyncStatus);
        var notifications = provider.GetRequiredService<INotificationService>();
        await notifications.Received(1).SendEventAsync(
            "analysis.cve-sync.failed",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ArtifactRepository_DoesNotExpireArtifactPinnedByPendingOutbox()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        await using var provider = CreateProvider(time, Options());
        await SeedReportAndOutboxAsync(provider, artifactExpired: true);

        await using var scope = provider.CreateAsyncScope();
        var repository = new ArtifactRepository(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        Assert.Empty(await repository.GetExpiredAsync(
            Now,
            100,
            TestContext.Current.CancellationToken));

        var item = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DependencyTrackOutboxItems.SingleAsync(TestContext.Current.CancellationToken);
        item.CompletedAt = Now;
        item.Status = DependencyTrackOutboxStatuses.Failed;
        item.PipelineArtifactId = null;
        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await repository.GetExpiredAsync(
            Now,
            100,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ArtifactRepository_DoesNotOfferPinnedArtifactForQuotaEviction()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        await using var provider = CreateProvider(time, Options());
        await SeedReportAndOutboxAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = new ArtifactRepository(db);

        Assert.Empty(await repository.GetProjectBuildArtifactsAsync(
            9,
            TestContext.Current.CancellationToken));

        var item = await db.DependencyTrackOutboxItems.SingleAsync(TestContext.Current.CancellationToken);
        item.CompletedAt = Now;
        item.Status = DependencyTrackOutboxStatuses.Succeeded;
        item.PipelineArtifactId = null;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await repository.GetProjectBuildArtifactsAsync(
            9,
            TestContext.Current.CancellationToken));
    }

    private static DependencyTrackOptions Options(int maxAttempts = 8, int retryBaseSeconds = 30) => new()
    {
        Enabled = true,
        MaxSubmissionAttempts = maxAttempts,
        RetryBaseSeconds = retryBaseSeconds
    };

    private static ServiceProvider CreateProvider(
        FakeTimeProvider time,
        DependencyTrackOptions options,
        IDependencyTrackSubmissionProcessor? processor = null)
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddSingleton<TimeProvider>(time);
        services.AddDbContext<AppDbContext>(builder =>
            builder.UseInMemoryDatabase(databaseName));
        services.AddScoped<DependencyTrackOutboxRepository>();
        services.AddScoped<IDependencyTrackOutbox, DependencyTrackOutbox>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton(processor ?? Substitute.For<IDependencyTrackSubmissionProcessor>());
        services.AddSingleton<IArtifactStorageService>(new MemoryArtifactStorage(BuildArtifact()));
        services.AddSingleton(Substitute.For<INotificationService>());
        return services.BuildServiceProvider();
    }

    private static DependencyTrackOutboxWorker Worker(
        ServiceProvider provider,
        FakeTimeProvider time,
        DependencyTrackOptions options) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(options),
            time,
            NullLogger<DependencyTrackOutboxWorker>.Instance);

    private static async Task<int> SeedReportAndOutboxAsync(
        ServiceProvider provider,
        bool artifactExpired = false)
    {
        var reportId = await SeedReportAsync(provider, artifactExpired);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDependencyTrackOutbox>()
            .EnqueueSbomAsync(reportId, TestContext.Current.CancellationToken);
        return reportId;
    }

    private static async Task<int> SeedReportAsync(
        ServiceProvider provider,
        bool artifactExpired = false)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Id = 7, Name = "Organization", Slug = "organization" };
        var project = new Project
        {
            Id = 9,
            Name = "Project",
            OrganizationId = 7,
            Organization = organization
        };
        var pipeline = new Pipeline { Id = 11, Name = "security", ProjectId = 9, Project = project };
        var run = new PipelineRun
        {
            Id = 13,
            PipelineId = 11,
            Pipeline = pipeline,
            CommitHash = new string('a', 40)
        };
        var artifact = new PipelineArtifact
        {
            Id = 17,
            PipelineId = 11,
            PipelineRunId = 13,
            ProjectId = 9,
            Pipeline = pipeline,
            PipelineRun = run,
            Project = project,
            Name = "analysis-syft",
            FilePath = "sbom.zip",
            RetentionExpiresAt = artifactExpired ? Now.AddMinutes(-1) : Now.AddDays(1)
        };
        var bytes = Encoding.UTF8.GetBytes(Sbom);
        var report = new AnalysisReport
        {
            Id = 19,
            OrganizationId = 7,
            ProjectId = 9,
            PipelineRunId = 13,
            PipelineArtifactId = 17,
            PipelineArtifact = artifact,
            PipelineRun = run,
            Project = project,
            Organization = organization,
            ScannerKey = "syft",
            ScannerName = "Syft",
            ScannerVersion = "1",
            Category = AnalysisCategory.Sbom,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.CycloneDxJson,
            ReportPath = "sbom.json",
            ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ContentSize = bytes.Length,
            CommitHash = run.CommitHash
        };
        db.AnalysisReports.Add(report);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return report.Id;
    }

    private static byte[] BuildArtifact()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("sbom.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(Sbom);
        }
        return output.ToArray();
    }

    private sealed class MemoryArtifactStorage(byte[] bytes) : IArtifactStorageService
    {
        public Task<(string RelativePath, string Sha256)> SaveArtifactAsync(
            int projectId,
            int pipelineId,
            int runId,
            string fileName,
            Stream content,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> AdoptVerifiedArtifactAsync(
            int projectId,
            int pipelineId,
            int runId,
            string fileName,
            VerifiedArtifactFile file,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteArtifactAsync(string filePath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Stream? OpenArtifact(string filePath) =>
            string.Equals(filePath, "sbom.zip", StringComparison.Ordinal)
                ? new MemoryStream(bytes, writable: false)
                : null;

        public long GetArtifactSize(string filePath) => bytes.LongLength;
    }
}
