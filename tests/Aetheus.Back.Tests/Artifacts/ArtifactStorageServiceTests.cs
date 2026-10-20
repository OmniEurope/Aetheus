// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Tests.Artifacts;

public class ArtifactStorageServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ArtifactStorageService _sut;

    public ArtifactStorageServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aetheus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ArtifactStorage:BasePath"] = _tempDir
            })
            .Build();

        _sut = new ArtifactStorageService(config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveArtifactAsync_ValidFileName_SavesFile()
    {
        var content = new MemoryStream("hello artifact"u8.ToArray());

        var (relativePath, sha256) = await _sut.SaveArtifactAsync(
            projectId: 1, pipelineId: 2, runId: 3, fileName: "build.zip", content, ct: TestContext.Current.CancellationToken);

        Assert.StartsWith(Path.Combine("1", "2", "3", "build."), relativePath, StringComparison.Ordinal);
        Assert.EndsWith(".zip", relativePath, StringComparison.Ordinal);

        var fullPath = Path.Combine(_tempDir, relativePath);
        Assert.True(File.Exists(fullPath));
        Assert.Equal("hello artifact", await File.ReadAllTextAsync(fullPath, cancellationToken: TestContext.Current.CancellationToken));

        // Checksum is the real SHA-256 of the stored bytes (no fabrication), lowercase hex.
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("hello artifact"u8.ToArray()));
        Assert.Equal(expected, sha256);
    }

    [Fact]
    public async Task SaveArtifactAsync_EmptyContent_ComputesEmptySha256()
    {
        // The SHA-256 of zero bytes is a well-known constant; asserting it proves the hash
        // reflects the actual stored bytes rather than a placeholder.
        var (_, sha256) = await _sut.SaveArtifactAsync(1, 2, 3, "empty.zip", new MemoryStream([]), ct: TestContext.Current.CancellationToken);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", sha256);
    }

    [Fact]
    public async Task SaveArtifactAsync_SameLogicalNameTwice_NeverOverwritesPublishedBytes()
    {
        var (firstPath, firstSha) = await _sut.SaveArtifactAsync(
            1, 2, 3, "build.zip", new MemoryStream("first"u8.ToArray()),
            ct: TestContext.Current.CancellationToken);
        var (secondPath, secondSha) = await _sut.SaveArtifactAsync(
            1, 2, 3, "build.zip", new MemoryStream("second"u8.ToArray()),
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual(firstPath, secondPath);
        Assert.NotEqual(firstSha, secondSha);
        Assert.Equal("first", await File.ReadAllTextAsync(
            Path.Combine(_tempDir, firstPath), TestContext.Current.CancellationToken));
        Assert.Equal("second", await File.ReadAllTextAsync(
            Path.Combine(_tempDir, secondPath), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\secret.txt")]
    [InlineData("sub/dir/file.txt")]
    public async Task SaveArtifactAsync_TraversalFileName_ThrowsBadRequest(string maliciousName)
    {
        var content = new MemoryStream("evil"u8.ToArray());

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SaveArtifactAsync(1, 2, 3, maliciousName, content, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteArtifactAsync_ExistingFile_DeletesFile()
    {
        // Arrange - save a file first
        var content = new MemoryStream("to-be-deleted"u8.ToArray());
        var (relativePath, _) = await _sut.SaveArtifactAsync(1, 2, 3, "artifact.bin", content, ct: TestContext.Current.CancellationToken);
        var fullPath = Path.Combine(_tempDir, relativePath);
        Assert.True(File.Exists(fullPath));

        // Act
        await _sut.DeleteArtifactAsync(relativePath, ct: TestContext.Current.CancellationToken);

        // Assert
        Assert.False(File.Exists(fullPath));
    }

    [Fact]
    public async Task AdoptVerifiedArtifactAsync_MovesTheFileUnderItsImmutableName()
    {
        // Recette R1-91: the assembled chunked upload is renamed into the store, not read and written again.
        var uploads = Path.Combine(_tempDir, ".uploads", "abc");
        Directory.CreateDirectory(uploads);
        var assembled = Path.Combine(uploads, "assembled");
        await File.WriteAllTextAsync(assembled, "verified bytes", TestContext.Current.CancellationToken);
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("verified bytes"u8.ToArray()));

        var relativePath = await _sut.AdoptVerifiedArtifactAsync(
            1, 2, 3, "build-3.zip", new VerifiedArtifactFile(assembled, 14, digest), TestContext.Current.CancellationToken);

        Assert.StartsWith(Path.Combine("1", "2", "3", $"build-3.{digest}."), relativePath, StringComparison.Ordinal);
        Assert.EndsWith(".zip", relativePath, StringComparison.Ordinal);
        Assert.False(File.Exists(assembled));
        Assert.Equal("verified bytes", await File.ReadAllTextAsync(Path.Combine(_tempDir, relativePath), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdoptVerifiedArtifactAsync_RefusesAFileOutsideTheStore()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"aetheus-outside-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(outside, "x", TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.AdoptVerifiedArtifactAsync(
                1, 2, 3, "build-3.zip", new VerifiedArtifactFile(outside, 1, new string('a', 64)), TestContext.Current.CancellationToken));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
