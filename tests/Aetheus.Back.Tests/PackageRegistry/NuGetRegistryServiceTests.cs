// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

public sealed class NuGetRegistryServiceTests
{
    private readonly IPackageRegistryRepository _repository =
        Substitute.For<IPackageRegistryRepository>();
    private readonly IPackageRegistryStorage _storage =
        Substitute.For<IPackageRegistryStorage>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly NuGetRegistryService _sut;

    public NuGetRegistryServiceTests()
    {
        _sut = new NuGetRegistryService(
            _repository,
            _storage,
            new NuGetPackageInspector(),
            new PackageRegistryPublishGate(),
            _audit,
            Substitute.For<IAdminChangeNotifier>());
    }

    [Fact]
    public async Task PublishAsync_NewPackage_PersistsInspectedPayloadAndAudit()
    {
        _repository.GetPackageForUpdateAsync(
                PackageRegistryKind.NuGet, "aetheus.telemetry", Arg.Any<CancellationToken>())
            .Returns((RegistryPackage?)null);
        _storage.SaveAsync(
                PackageRegistryKind.NuGet,
                "aetheus.telemetry",
                "1.2.3-beta.1",
                ".nupkg",
                Arg.Any<Stream>(),
                Arg.Any<CancellationToken>())
            .Returns(new StoredPackagePayload(
                "nuget/aetheus.telemetry/1.2.3-beta.1/package.nupkg",
                123,
                new string('a', 64),
                new string('b', 40),
                "sha512-value"));

        await _sut.PublishAsync(
            CreateNuGetPackage,
            "alice",
            TestContext.Current.CancellationToken);

        await _repository.Received(1).AddPackageAsync(
            Arg.Is<RegistryPackage>(package =>
                package.Name == "Aetheus.Telemetry"
                && package.NormalizedName == "aetheus.telemetry"
                && package.AllowAnonymousDownload
                && package.Description == "Internal telemetry package."),
            Arg.Any<CancellationToken>());
        await _repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "Published",
            "NuGetPackage",
            Arg.Any<int>(),
            "Aetheus.Telemetry@1.2.3-beta.1",
            Arg.Any<CancellationToken>());
        var added = Assert.Single(
            _repository.ReceivedCalls()
                .Where(call => call.GetMethodInfo().Name == nameof(IPackageRegistryRepository.AddPackageAsync))
                .Select(call => (RegistryPackage)call.GetArguments()[0]!));
        var version = Assert.Single(added.Versions);
        Assert.Equal("alice", version.PublishedBy);
        Assert.True(version.IsPrerelease);
        Assert.Equal(123, version.SizeBytes);
        Assert.Contains("OpenTelemetry", version.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAsync_ExistingVersion_RejectsBeforeWritingStorage()
    {
        _repository.GetPackageForUpdateAsync(
                PackageRegistryKind.NuGet, "aetheus.telemetry", Arg.Any<CancellationToken>())
            .Returns(new RegistryPackage
            {
                Name = "Aetheus.Telemetry",
                NormalizedName = "aetheus.telemetry",
                Versions =
                [
                    new RegistryPackageVersion { NormalizedVersion = "1.2.3-beta.1" }
                ]
            });

        await Assert.ThrowsAsync<ConflictException>(() => _sut.PublishAsync(
            CreateNuGetPackage,
            "alice",
            TestContext.Current.CancellationToken));

        await _storage.DidNotReceiveWithAnyArgs().SaveAsync(
            default,
            default!,
            default!,
            default!,
            default!,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishAsync_SaveFailure_DeletesOnlyNewPayload()
    {
        _repository.GetPackageForUpdateAsync(
                PackageRegistryKind.NuGet, "aetheus.telemetry", Arg.Any<CancellationToken>())
            .Returns(new RegistryPackage
            {
                Id = 7,
                Name = "Aetheus.Telemetry",
                NormalizedName = "aetheus.telemetry"
            });
        _storage.SaveAsync(
                PackageRegistryKind.NuGet,
                "aetheus.telemetry",
                "1.2.3-beta.1",
                ".nupkg",
                Arg.Any<Stream>(),
                Arg.Any<CancellationToken>())
            .Returns(new StoredPackagePayload(
                "new-package.nupkg",
                123,
                new string('a', 64),
                new string('b', 40),
                "sha512-value",
                CreatedNew: true));
        _repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("database unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.PublishAsync(
            CreateNuGetPackage,
            "alice",
            TestContext.Current.CancellationToken));

        await _storage.Received(1).DeleteAsync(
            "new-package.nupkg",
            CancellationToken.None);
    }

    [Fact]
    public async Task GetVersionsAsync_NormalizesAndOrdersSemanticVersions()
    {
        _repository.GetVersionNamesAsync(
                PackageRegistryKind.NuGet, "sample", Arg.Any<CancellationToken>())
            .Returns(["2.0.0", "1.10", "1.2.3-BETA"]);

        var versions = await _sut.GetVersionsAsync(
            "Sample",
            TestContext.Current.CancellationToken);

        Assert.Equal(["1.2.3-beta", "1.10.0", "2.0.0"], versions);
    }

    [Fact]
    public async Task GetVersionsAsync_MissingPackage_ReturnsNull()
    {
        _repository.GetVersionNamesAsync(
                PackageRegistryKind.NuGet, "missing", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>?)null);

        Assert.Null(await _sut.GetVersionsAsync(
            "Missing",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetVersionAsync_NormalizesPackageAndVersion()
    {
        var stored = new RegistryPackageVersion { Id = 9 };
        _repository.GetVersionAsync(
                PackageRegistryKind.NuGet,
                "sample",
                "1.2.0",
                Arg.Any<CancellationToken>())
            .Returns(stored);

        var result = await _sut.GetVersionAsync(
            "Sample",
            "1.2",
            TestContext.Current.CancellationToken);

        Assert.Same(stored, result);
    }

    [Fact]
    public async Task GetRegistrationAsync_UsesLatestMetadataAndOrdersVersions()
    {
        var oldMetadata = Metadata("old authors", "old tags", "old manifest");
        var latestMetadata = Metadata("new authors", "new tags", "new manifest");
        _repository.GetRegistrationPackageAsync(
                PackageRegistryKind.NuGet,
                "sample",
                null,
                Arg.Any<CancellationToken>())
            .Returns(new PackageRegistryRegistrationPackage(
                "Sample",
                "Description",
                [
                    new PackageRegistryRegistrationVersion(
                        "2.0.0", false, new DateTime(2026, 2, 1), latestMetadata),
                    new PackageRegistryRegistrationVersion(
                        "1.0.0", true, new DateTime(2026, 1, 1), oldMetadata)
                ]));

        var result = await _sut.GetRegistrationAsync(
            "Sample",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("new authors", result.Authors);
        Assert.Equal("new tags", result.Tags);
        Assert.Equal(["1.0.0", "2.0.0"], result.Versions.Select(version => version.Version));
        Assert.False(result.Versions[1].IsListed);
    }

    [Fact]
    public async Task GetRegistrationAsync_MissingPackage_ReturnsNull()
    {
        _repository.GetRegistrationPackageAsync(
                PackageRegistryKind.NuGet,
                "missing",
                null,
                Arg.Any<CancellationToken>())
            .Returns((PackageRegistryRegistrationPackage?)null);

        Assert.Null(await _sut.GetRegistrationAsync(
            "Missing",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRegistrationVersionAsync_MapsSelectedVersion()
    {
        _repository.GetRegistrationPackageAsync(
                PackageRegistryKind.NuGet,
                "sample",
                "1.2.0",
                Arg.Any<CancellationToken>())
            .Returns(new PackageRegistryRegistrationPackage(
                "Sample",
                "Description",
                [
                    new PackageRegistryRegistrationVersion(
                        "1.2.0",
                        true,
                        new DateTime(2026, 1, 2),
                        Metadata("authors", "tags", "manifest"))
                ]));

        var result = await _sut.GetRegistrationVersionAsync(
            "Sample",
            "1.2",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("1.2.0", Assert.Single(result.Versions).Version);
        Assert.Equal("net8.0", Assert.Single(result.Versions[0].DependencyGroups).TargetFramework);
    }

    [Fact]
    public async Task GetRegistrationVersionAsync_EmptyPackage_ReturnsNull()
    {
        _repository.GetRegistrationPackageAsync(
                PackageRegistryKind.NuGet,
                "sample",
                "1.2.0",
                Arg.Any<CancellationToken>())
            .Returns(new PackageRegistryRegistrationPackage("Sample", null, []));

        Assert.Null(await _sut.GetRegistrationVersionAsync(
            "Sample",
            "1.2",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAsync_FiltersUnlistedAndPrereleaseVersions()
    {
        _repository.SearchAsync(
                PackageRegistryKind.NuGet,
                "telemetry",
                0,
                200,
                false,
                Arg.Any<CancellationToken>())
            .Returns((
            [
                new PackageRegistrySearchPackage(
                    "Aetheus.Telemetry",
                    "Description",
                    null,
                    [
                        new PackageRegistrySearchVersion(
                            10, "2.0.0-beta", "2.0.0-beta", true, true, new DateTime(2026, 2, 1)),
                        new PackageRegistrySearchVersion(
                            11, "1.5.0", "1.5.0", false, false, new DateTime(2026, 1, 2)),
                        new PackageRegistrySearchVersion(
                            12, "1.2.0", "1.2.0", false, true, new DateTime(2026, 1, 1))
                    ])
            ],
            1));
        _repository.GetVersionMetadataAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 12 })),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>
            {
                [12] = Metadata("authors", "tags", "manifest")
            });

        var result = await _sut.SearchAsync(
            "telemetry",
            -4,
            500,
            includePrerelease: false,
            TestContext.Current.CancellationToken);

        var package = Assert.Single(result.Packages);
        Assert.Equal(1, result.TotalHits);
        Assert.Equal("1.2.0", package.Version);
        Assert.Equal(["1.2.0"], package.Versions);
    }

    [Fact]
    public async Task SetListedAsync_UpdatesStateAndAuditsActor()
    {
        var package = new RegistryPackage
        {
            Id = 7,
            Name = "Sample",
            Versions =
            [
                new RegistryPackageVersion
                {
                    Version = "1.2.0",
                    NormalizedVersion = "1.2.0",
                    IsListed = false
                }
            ]
        };
        _repository.GetPackageForUpdateAsync(
                PackageRegistryKind.NuGet, "sample", Arg.Any<CancellationToken>())
            .Returns(package);

        var updated = await _sut.SetListedAsync(
            "Sample",
            "1.2",
            listed: true,
            "alice",
            TestContext.Current.CancellationToken);

        Assert.True(updated);
        Assert.True(package.Versions[0].IsListed);
        await _audit.Received(1).LogAsync(
            "Relisted",
            "NuGetPackage",
            7,
            "Sample@1.2.0 by alice",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetListedAsync_MissingVersion_ReturnsFalse()
    {
        _repository.GetPackageForUpdateAsync(
                PackageRegistryKind.NuGet, "sample", Arg.Any<CancellationToken>())
            .Returns((RegistryPackage?)null);

        Assert.False(await _sut.SetListedAsync(
            "Sample",
            "1.2",
            listed: false,
            "alice",
            TestContext.Current.CancellationToken));
        await _repository.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ContentAccess_DelegatesStorageAndDeserializesManifest()
    {
        var stream = new MemoryStream([1, 2, 3]);
        _storage.OpenRead("sample.nupkg").Returns(stream);
        var version = new RegistryPackageVersion
        {
            FilePath = "sample.nupkg",
            Metadata = Metadata("authors", "tags", "manifest XML")
        };

        Assert.Same(stream, _sut.OpenContent(version));
        Assert.Equal("manifest XML", _sut.ReadManifest(version));
    }

    private static string Metadata(string authors, string tags, string manifest) =>
        JsonSerializer.Serialize(new
        {
            Manifest = manifest,
            Authors = authors,
            Tags = tags,
            DependencyGroups = new[]
            {
                new
                {
                    TargetFramework = "net8.0",
                    Dependencies = new[]
                    {
                        new { Id = "OpenTelemetry", Range = "[1.9.0, 2.0.0)" }
                    }
                }
            }
        });

    private static MemoryStream CreateNuGetPackage()
    {
        const string nuspec = """
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Aetheus.Telemetry</id>
                <version>1.2.3-beta.1</version>
                <authors>Aetheus</authors>
                <description>Internal telemetry package.</description>
                <tags>internal telemetry</tags>
                <dependencies>
                  <group targetFramework="net8.0">
                    <dependency id="OpenTelemetry" version="[1.9.0, 2.0.0)" />
                  </group>
                </dependencies>
              </metadata>
            </package>
            """;
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("Aetheus.Telemetry.nuspec");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(nuspec);
        }
        stream.Position = 0;
        return stream;
    }
}
