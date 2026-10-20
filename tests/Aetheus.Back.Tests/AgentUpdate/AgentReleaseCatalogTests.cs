// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.AgentUpdate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public sealed class AgentReleaseCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"aetheus-release-catalog-{Guid.NewGuid():N}");

    [Fact]
    public async Task ProductionStartup_ValidatesRealArchiveSizeAndSha256()
    {
        var archive = WriteArchive("real agent archive");
        WriteManifest(archive);
        var catalog = CreateCatalog();

        await catalog.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1.0.0", catalog.Current.SoftwareVersion);
        Assert.Single(catalog.Current.Archives);
    }

    [Fact]
    public async Task ProductionStartup_AcceptsUpgradeBridgeContainingCurrentProtocol()
    {
        var archive = WriteArchive("bridge agent archive");
        WriteManifest(
            archive,
            minimumSupportedProtocol: AgentProtocol.CurrentVersion - 1,
            maximumSupportedProtocol: AgentProtocol.CurrentVersion);
        var catalog = CreateCatalog();

        await catalog.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AgentProtocol.CurrentVersion - 1, catalog.Current.MinimumSupportedProtocol);
    }

    [Fact]
    public async Task ProductionStartup_RejectsWindowExcludingCurrentProtocol()
    {
        var archive = WriteArchive("incompatible agent archive");
        WriteManifest(
            archive,
            minimumSupportedProtocol: AgentProtocol.CurrentVersion + 1,
            maximumSupportedProtocol: AgentProtocol.CurrentVersion + 1);
        var catalog = CreateCatalog();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("does not include", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionStartup_RejectsHashMismatch()
    {
        var archive = WriteArchive("tampered archive");
        WriteManifest(archive with { Sha256 = new string('0', 64) });
        var catalog = CreateCatalog();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("SHA-256 mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionStartup_RejectsMissingManifest()
    {
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot", "downloads"));
        var catalog = CreateCatalog();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("manifest is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionStartup_RejectsAnArchiveWithoutRequiredFields()
    {
        var archive = WriteArchive("incomplete archive");
        WriteManifest(archive with { Platform = string.Empty });
        var catalog = CreateCatalog();

        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(
            () => catalog.StartAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escape.tar.gz", null, null, "must not contain a path")]
    [InlineData(null, "macos", null, "Unsupported agent archive platform: macos")]
    [InlineData(null, null, "riscv", "Unsupported agent archive architecture: riscv")]
    public async Task ProductionStartup_RejectsAnArchiveOutsideTheSupportedShape(
        string? fileName, string? platform, string? architecture, string expectedMessage)
    {
        var archive = WriteArchive("shaped archive");
        WriteManifest(archive with
        {
            FileName = fileName ?? archive.FileName,
            Platform = platform ?? archive.Platform,
            Architecture = architecture ?? archive.Architecture
        });
        var catalog = CreateCatalog();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionStartup_RejectsAMissingArchiveFile()
    {
        var archive = WriteArchive("vanished archive");
        WriteManifest(archive);
        File.Delete(Path.Combine(_root, "wwwroot", "downloads", "releases", "1.0.0", archive.FileName));
        var catalog = CreateCatalog();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("archive is missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionStartup_RejectsASizeMismatch()
    {
        var archive = WriteArchive("truncated archive");
        WriteManifest(archive with { SizeBytes = archive.SizeBytes + 1 });
        var catalog = CreateCatalog();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("size mismatch", exception.Message, StringComparison.Ordinal);
    }

    private AgentReleaseArchiveDto WriteArchive(string content)
    {
        var releaseDirectory = Path.Combine(
            _root,
            "wwwroot",
            "downloads",
            "releases",
            "1.0.0");
        Directory.CreateDirectory(releaseDirectory);
        var bytes = Encoding.UTF8.GetBytes(content);
        const string fileName = "aetheus-agent-linux-x64.tar.gz";
        File.WriteAllBytes(Path.Combine(releaseDirectory, fileName), bytes);
        return new AgentReleaseArchiveDto
        {
            Platform = "linux",
            Architecture = "x64",
            FileName = fileName,
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
    }

    private void WriteManifest(
        AgentReleaseArchiveDto archive,
        int minimumSupportedProtocol = AgentProtocol.MinimumSupportedVersion,
        int maximumSupportedProtocol = AgentProtocol.MaximumSupportedVersion)
    {
        var manifest = new AgentReleaseManifestDto
        {
            SoftwareVersion = "1.0.0",
            ProtocolVersion = AgentProtocol.CurrentVersion,
            MinimumSupportedProtocol = minimumSupportedProtocol,
            MaximumSupportedProtocol = maximumSupportedProtocol,
            SoftwareCapabilities = AgentCapabilities.SoftwareCapabilities
                .Order(StringComparer.Ordinal)
                .ToList(),
            Archives = [archive],
            ProducedAtUtc = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc),
            Commit = new string('a', 40)
        };
        var downloads = Path.Combine(_root, "wwwroot", "downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(
            Path.Combine(downloads, "agent-release-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private AgentReleaseCatalog CreateCatalog()
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_root);
        environment.EnvironmentName.Returns("Production");
        return new AgentReleaseCatalog(
            environment,
            NullLogger<AgentReleaseCatalog>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
