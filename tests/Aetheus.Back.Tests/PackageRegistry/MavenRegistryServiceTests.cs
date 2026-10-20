// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Data;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

/// <summary>
/// The Maven repository over the real repository (EF InMemory) and the real disk storage: what
/// <c>mvn deploy</c> puts is stored once and immutably, the metadata and checksums read back are
/// generated from that state, and the reconciler keeps every stored Maven file.
/// </summary>
public sealed class MavenRegistryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "aetheus-maven-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly PackageRegistryRepository _repository;
    private readonly PackageRegistryStorage _storage;
    private readonly MavenRegistryService _service;

    public MavenRegistryServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, _clock);
        _repository = new PackageRegistryRepository(_db);
        _storage = new PackageRegistryStorage(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PackageRegistry:BasePath"] = _directory })
            .Build());
        _service = new MavenRegistryService(_repository, _storage, new PackageRegistryPublishGate(),
            Substitute.For<IAuditService>(), Substitute.For<IAdminChangeNotifier>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Pom(string version, string artifactId = "demo") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <project xmlns="http://maven.apache.org/POM/4.0.0">
          <modelVersion>4.0.0</modelVersion>
          <groupId>org.example</groupId><artifactId>{artifactId}</artifactId><version>{version}</version>
          <description>Demo library</description>
        </project>
        """;

    private static MavenRepositoryPath Parse(string raw)
        => MavenRepositoryPath.TryParse(raw, out var parsed, out var error) ? parsed! : throw new InvalidOperationException(error);

    private Task PutAsync(string raw, string body)
        => _service.PutAsync(Parse(raw), new MemoryStream(Encoding.UTF8.GetBytes(body)), "admin", Ct);

    private async Task<string> ReadAsync(string raw)
    {
        var content = await _service.GetAsync(Parse(raw), Ct) ?? throw new InvalidOperationException($"{raw} not found");
        if (content.Bytes is not null)
            return Encoding.UTF8.GetString(content.Bytes);
        await using var stream = _service.OpenStored(content.StoragePath!)!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }

    private static string Sha1(string text) => Digest(HashAlgorithmName.SHA1, text);

    private static string Digest(HashAlgorithmName algorithm, string text)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [Fact]
    public async Task Deploy_ThenRead_ServesTheFilesTheirChecksumsAndGeneratedMetadata()
    {
        await PutAsync("org/example/demo/1.0/demo-1.0.jar", "jar-1.0");
        await PutAsync("org/example/demo/1.0/demo-1.0.jar.sha1", Sha1("jar-1.0"));
        await PutAsync("org/example/demo/1.0/demo-1.0.pom", Pom("1.0"));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await PutAsync("org/example/demo/1.10/demo-1.10.jar", "jar-1.10");
        await PutAsync("org/example/demo/1.10/demo-1.10.pom", Pom("1.10"));
        // The client's own metadata is accepted and ignored: the registry writes its own.
        await PutAsync("org/example/demo/maven-metadata.xml", "<metadata><versioning><release>9.9</release></versioning></metadata>");

        Assert.Equal("jar-1.10", await ReadAsync("org/example/demo/1.10/demo-1.10.jar"));
        Assert.Equal(Sha1("jar-1.10"), await ReadAsync("org/example/demo/1.10/demo-1.10.jar.sha1"));
        Assert.Equal(Digest(HashAlgorithmName.MD5, "jar-1.0"),
            await ReadAsync("org/example/demo/1.0/demo-1.0.jar.md5"));

        var metadata = await ReadAsync("org/example/demo/maven-metadata.xml");
        var versioning = XDocument.Parse(metadata).Root!.Element("versioning")!;
        Assert.Equal("1.10", versioning.Element("release")!.Value);
        Assert.Equal(["1.0", "1.10"], versioning.Descendants("version").Select(element => element.Value));
        Assert.Equal("20261017120500", versioning.Element("lastUpdated")!.Value);
        Assert.Equal(Sha1(metadata), await ReadAsync("org/example/demo/maven-metadata.xml.sha1"));

        var package = await _repository.GetPackageWithFilesAsync(PackageRegistryKind.Maven, "org.example:demo", false, Ct);
        Assert.Equal("Demo library", package!.Description);
        var v10 = package.Versions.Single(version => version.Version == "1.0");
        Assert.Equal(2, v10.Files.Count);
        Assert.EndsWith("demo-1.0.jar", v10.FilePath, StringComparison.Ordinal);
        Assert.Equal(v10.Files.Sum(file => file.SizeBytes), v10.SizeBytes);
        Assert.False(v10.IsPrerelease);
    }

    [Fact]
    public async Task Redeploy_SameBytesIsAccepted_DifferentBytesIsAConflict()
    {
        await PutAsync("org/example/demo/1.0/demo-1.0.jar", "original");
        await PutAsync("org/example/demo/1.0/demo-1.0.jar", "original");

        await Assert.ThrowsAsync<ConflictException>(() => PutAsync("org/example/demo/1.0/demo-1.0.jar", "tampered"));
        Assert.Equal("original", await ReadAsync("org/example/demo/1.0/demo-1.0.jar"));
        Assert.Equal(1, await _db.RegistryPackageFiles.CountAsync(Ct));
    }

    [Fact]
    public async Task UploadedChecksum_MustMatchTheStoredFile()
    {
        await PutAsync("org/example/demo/1.0/demo-1.0.jar", "content");
        await PutAsync("org/example/demo/1.0/demo-1.0.jar.sha1", Sha1("content").ToUpperInvariant() + "  demo-1.0.jar");

        await Assert.ThrowsAsync<BadRequestException>(() => PutAsync("org/example/demo/1.0/demo-1.0.jar.sha1", Sha1("other")));
        await Assert.ThrowsAsync<NotFoundException>(() => PutAsync("org/example/demo/1.0/demo-1.0-sources.jar.sha1", Sha1("x")));
    }

    [Fact]
    public async Task Pom_MustDescribeTheCoordinatesItIsDeployedTo()
    {
        var wrongArtifact = await Assert.ThrowsAsync<BadRequestException>(
            () => PutAsync("org/example/demo/1.0/demo-1.0.pom", Pom("1.0", artifactId: "other")));
        Assert.Contains("artifactId", wrongArtifact.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<BadRequestException>(() => PutAsync("org/example/demo/1.0/demo-1.0.pom", Pom("2.0")));
        await Assert.ThrowsAsync<BadRequestException>(() => PutAsync("org/example/demo/1.0/demo-1.0.pom", "<not-xml"));
        await Assert.ThrowsAsync<BadRequestException>(() => PutAsync(
            "org/example/demo/1.0/demo-1.0.pom",
            """<?xml version="1.0"?><!DOCTYPE p [<!ENTITY x SYSTEM "file:///etc/passwd">]><project>&x;</project>"""));
        Assert.Equal(0, await _db.RegistryPackageFiles.CountAsync(Ct));
    }

    [Fact]
    public async Task UploadedMetadata_MustBeWellFormedXml()
        => await Assert.ThrowsAsync<BadRequestException>(() => PutAsync("org/example/demo/maven-metadata.xml", "<metadata"));

    [Fact]
    public async Task SnapshotBuilds_AreListedInTheSnapshotMetadata_NewestFirstInPriority()
    {
        await PutAsync("org/example/demo/2.0-SNAPSHOT/demo-2.0-20261017.100000-1.jar", "build-1");
        await PutAsync("org/example/demo/2.0-SNAPSHOT/demo-2.0-20261017.100000-1.pom", Pom("2.0-SNAPSHOT"));
        _clock.Advance(TimeSpan.FromHours(1));
        await PutAsync("org/example/demo/2.0-SNAPSHOT/demo-2.0-20261017.110000-2.jar", "build-2");
        await PutAsync("org/example/demo/2.0-SNAPSHOT/demo-2.0-20261017.110000-2.pom", Pom("2.0-SNAPSHOT"));

        var snapshot = XDocument.Parse(await ReadAsync("org/example/demo/2.0-SNAPSHOT/maven-metadata.xml")).Root!;
        Assert.Equal("2", snapshot.Descendants("buildNumber").Single().Value);
        Assert.Equal("20261017.110000", snapshot.Descendants("timestamp").Single().Value);
        Assert.All(snapshot.Descendants("value"), value => Assert.Equal("2.0-20261017.110000-2", value.Value));

        var artifact = XDocument.Parse(await ReadAsync("org/example/demo/maven-metadata.xml")).Root!;
        Assert.Equal("2.0-SNAPSHOT", artifact.Descendants("latest").Single().Value);
        Assert.Empty(artifact.Descendants("release"));

        var version = (await _repository.GetPackageWithFilesAsync(PackageRegistryKind.Maven, "org.example:demo", false, Ct))!
            .Versions.Single();
        Assert.True(version.IsPrerelease);
        Assert.EndsWith("demo-2.0-20261017.110000-2.jar", version.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnlistedVersion_LeavesTheMetadata_ButStaysDownloadable()
    {
        await PutAsync("org/example/demo/1.0/demo-1.0.jar", "one");
        await PutAsync("org/example/demo/1.1/demo-1.1.jar", "two");
        var version = await _db.RegistryPackageVersions.SingleAsync(item => item.Version == "1.1", Ct);
        version.IsListed = false;
        await _db.SaveChangesAsync(Ct);

        var metadata = XDocument.Parse(await ReadAsync("org/example/demo/maven-metadata.xml")).Root!;
        Assert.Equal(["1.0"], metadata.Descendants("version").Select(element => element.Value));
        Assert.Equal("two", await ReadAsync("org/example/demo/1.1/demo-1.1.jar"));
    }

    [Fact]
    public async Task UnknownPaths_AreNotFound()
    {
        Assert.Null(await _service.GetAsync(Parse("org/example/demo/maven-metadata.xml"), Ct));
        await PutAsync("org/example/demo/1.0/demo-1.0.jar", "one");
        Assert.Null(await _service.GetAsync(Parse("org/example/demo/1.0/demo-1.0-sources.jar"), Ct));
        Assert.Null(await _service.GetAsync(Parse("org/example/demo/2.0/demo-2.0.jar"), Ct));
        Assert.Null(await _service.GetAsync(Parse("org/example/demo/1.0-SNAPSHOT/maven-metadata.xml"), Ct));
    }

    [Fact]
    public async Task Reconciler_KeepsEveryStoredMavenFile()
    {
        await PutAsync("org/example/demo/1.0/demo-1.0.jar", "jar");
        await PutAsync("org/example/demo/1.0/demo-1.0.pom", Pom("1.0"));

        var referenced = await _repository.GetStoredFilePathsAsync(Ct);
        var deleted = await _storage.ReconcileAsync(referenced, DateTime.UtcNow.AddDays(1), Ct);

        Assert.Equal(0, deleted);
        Assert.Equal("jar", await ReadAsync("org/example/demo/1.0/demo-1.0.jar"));
        Assert.Contains(await ReadAsync("org/example/demo/1.0/demo-1.0.pom"), Pom("1.0"), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
