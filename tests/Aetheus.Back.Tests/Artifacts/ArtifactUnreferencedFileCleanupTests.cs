// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Artifacts;

/// <summary>
/// Recette R1-73: payload files that no artifact row references are deleted after 30 days, on a real
/// temporary store; a file a row records, a recent file and anything outside the payload layout stay.
/// </summary>
public sealed class ArtifactUnreferencedFileCleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-artifacts-{Guid.NewGuid():N}");
    private readonly IArtifactRepository _repository = Substitute.For<IArtifactRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly ArtifactCleanupService _sut;

    public ArtifactUnreferencedFileCleanupTests()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ArtifactStorage:BasePath"] = _root })
            .Build();
        var scopeFactory = new ServiceCollection()
            .AddScoped(_ => _repository)
            .AddScoped(_ => _audit)
            .AddScoped<IArtifactStorageService>(_ => new ArtifactStorageService(configuration))
            .AddSingleton(new ArtifactFileInventory(configuration))
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        _sut = new ArtifactCleanupService(
            scopeFactory, NullLogger<ArtifactCleanupService>.Instance, new FakeTimeProvider(new DateTimeOffset(Now)),
            Substitute.For<IPostgresLeaderLease>(), Substitute.For<IChunkedArtifactUploadService>());
        _repository.GetAllFilePathsAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Write(string relativePath, int ageDays)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "payload");
        File.SetLastWriteTimeUtc(fullPath, Now.AddDays(-ageDays));
        return fullPath;
    }

    private Task<int> SweepAsync() => _sut.PurgeUnreferencedFilesAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnOldFileNoRowReferences_IsDeleted_AndAudited()
    {
        var orphan = Write(Path.Combine("1", "2", "3", "build.abc.zip"), ageDays: 31);

        Assert.Equal(1, await SweepAsync());

        Assert.False(File.Exists(orphan));
        await _audit.Received(1).LogAsync(
            "ArtifactUnreferencedFilesPurged", "PipelineArtifact", null,
            Arg.Is<string>(details => details.StartsWith("deleted=1;", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFileYoungerThanThirtyDays_IsKept()
    {
        var recent = Write(Path.Combine("1", "2", "3", "build.abc.zip"), ageDays: 29);

        Assert.Equal(0, await SweepAsync());

        Assert.True(File.Exists(recent));
        await _audit.DidNotReceive().LogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOldFileARowRecords_IsKept_HoweverThePathIsSpelled()
    {
        var exact = Write(Path.Combine("1", "2", "3", "exact.zip"), ageDays: 400);
        var dotted = Write(Path.Combine("1", "2", "4", "dotted.zip"), ageDays: 400);
        var otherCase = Write(Path.Combine("1", "2", "5", "case.zip"), ageDays: 400);
        _repository.GetAllFilePathsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Path.Combine("1", "2", "3", "exact.zip"),
            "./1/2/4/dotted.zip",
            Path.Combine("1", "2", "5", "CASE.zip")
        ]);

        Assert.Equal(0, await SweepAsync());

        Assert.True(File.Exists(exact));
        Assert.True(File.Exists(dotted));
        Assert.True(File.Exists(otherCase));
    }

    [Fact]
    public async Task AFileRecordedAfterTheSnapshot_IsKept_ByTheCheckBeforeDelete()
    {
        var relativePath = Path.Combine("1", "2", "3", "raced.zip");
        var raced = Write(relativePath, ageDays: 31);
        _repository.IsFilePathReferencedAsync(relativePath, Arg.Any<CancellationToken>()).Returns(true);

        Assert.Equal(0, await SweepAsync());

        Assert.True(File.Exists(raced));
    }

    [Fact]
    public async Task FilesOutsideThePayloadLayout_AreNeverListed()
    {
        var upload = Write(Path.Combine(".uploads", "abc", "part-000001"), ageDays: 400);
        var transfer = Write(Path.Combine("1", "2", "3", ".0123.upload"), ageDays: 400);
        var foreign = Write(Path.Combine("notes", "readme.txt"), ageDays: 400);
        var shallow = Write(Path.Combine("1", "loose.zip"), ageDays: 400);

        Assert.Equal(0, await SweepAsync());

        Assert.True(File.Exists(upload));
        Assert.True(File.Exists(transfer));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(shallow));
    }
}
