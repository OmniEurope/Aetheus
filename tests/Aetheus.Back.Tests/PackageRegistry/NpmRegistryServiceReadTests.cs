// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json.Nodes;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

/// <summary>
/// The npm registry read surface an npm client actually calls: the packument, a single version, the
/// tarball lookup and search. What matters is the dist-tag/selector resolution and the unlisted
/// filter, because an unlisted version must stay invisible to <c>npm install</c> while still
/// existing in the store.
/// </summary>
public sealed class NpmRegistryServiceReadTests
{
    private readonly IPackageRegistryRepository _repository = Substitute.For<IPackageRegistryRepository>();
    private readonly IPackageRegistryStorage _storage = Substitute.For<IPackageRegistryStorage>();
    private readonly NpmRegistryService _service;

    public NpmRegistryServiceReadTests()
    {
        _service = new NpmRegistryService(
            _repository,
            _storage,
            new NpmPackageInspector(),
            new PackageRegistryPublishGate(),
            Substitute.For<IAuditService>(),
            new ConfigurationBuilder().Build(),
            Substitute.For<IAdminChangeNotifier>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string RegistryBaseUrl = "https://registry.test/npm/";

    private static RegistryPackageVersion Version(
        int id, string version, bool listed = true, DateTime? createdAt = null,
        string? metadata = null) =>
        new()
        {
            Id = id,
            RegistryPackageId = 1,
            Version = version,
            NormalizedVersion = version.ToLowerInvariant(),
            IsListed = listed,
            Sha1 = "sha1-value",
            Integrity = "sha512-value",
            FilePath = $"npm/left-pad/{version}.tgz",
            Metadata = metadata ?? $"{{\"name\":\"left-pad\",\"version\":\"{version}\"}}",
            CreatedAt = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    private static RegistryPackage Package(
        string name = "left-pad", string? distTags = null, params RegistryPackageVersion[] versions) =>
        new()
        {
            Id = 1,
            Kind = PackageRegistryKind.Npm,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Description = "pads on the left",
            DistTagsJson = distTags,
            Versions = [.. versions]
        };

    private void HavePackage(RegistryPackage package) =>
        _repository.GetPackageAsync(PackageRegistryKind.Npm, package.NormalizedName, Arg.Any<CancellationToken>())
            .Returns(package);

    // ---------- packument ----------

    [Fact]
    public async Task GetPackumentAsync_ReturnsNullForAnUnknownPackage()
    {
        Assert.Null(await _service.GetPackumentAsync("left-pad", RegistryBaseUrl, Ct));
    }

    [Fact]
    public async Task GetPackumentAsync_ReturnsNullWhenEveryVersionIsUnlisted()
    {
        HavePackage(Package(versions: Version(1, "1.0.0", listed: false)));

        Assert.Null(await _service.GetPackumentAsync("left-pad", RegistryBaseUrl, Ct));
    }

    [Fact]
    public async Task GetPackumentAsync_PublishesOnlyTheListedVersionsWithTheirTarballUrls()
    {
        HavePackage(Package(
            distTags: "{\"latest\":\"2.0.0\"}",
            versions: [Version(1, "1.0.0"), Version(2, "2.0.0"), Version(3, "3.0.0-beta", listed: false)]));

        var packument = await _service.GetPackumentAsync("left-pad", RegistryBaseUrl, Ct);

        Assert.Equal("left-pad", packument!["name"]!.GetValue<string>());
        Assert.Equal("pads on the left", packument["description"]!.GetValue<string>());
        var versions = packument["versions"]!.AsObject();
        Assert.Equal(["1.0.0", "2.0.0"], versions.Select(pair => pair.Key).Order());
        Assert.Equal(
            "https://registry.test/npm/left-pad/-/left-pad-2.0.0.tgz",
            versions["2.0.0"]!["dist"]!["tarball"]!.GetValue<string>());
        Assert.Equal("sha1-value", versions["2.0.0"]!["dist"]!["shasum"]!.GetValue<string>());
        Assert.Contains("1.0.0", packument["time"]!.AsObject().Select(pair => pair.Key));
    }

    [Fact]
    public async Task GetPackumentAsync_FlagsOnlyTheDeprecatedVersionWithTheDeprecatedField()
    {
        var deprecated = Version(1, "1.0.0");
        deprecated.IsDeprecated = true;
        HavePackage(Package(distTags: "{\"latest\":\"2.0.0\"}", versions: [deprecated, Version(2, "2.0.0")]));

        var packument = await _service.GetPackumentAsync("left-pad", RegistryBaseUrl, Ct);

        var versions = packument!["versions"]!.AsObject();
        Assert.Equal(Aetheus.Back.Components.PackageRegistry.PackageRegistryDefaults.DeprecationMessage, versions["1.0.0"]!["deprecated"]!.GetValue<string>());
        Assert.Null(versions["2.0.0"]!["deprecated"]);
    }

    [Fact]
    public async Task GetPackumentAsync_DropsADistTagPointingAtAnUnlistedVersion()
    {
        HavePackage(Package(
            distTags: "{\"latest\":\"1.0.0\",\"next\":\"2.0.0-beta\"}",
            versions: [Version(1, "1.0.0"), Version(2, "2.0.0-beta", listed: false)]));

        var distTags = (await _service.GetPackumentAsync("left-pad", RegistryBaseUrl, Ct))!["dist-tags"]!.AsObject();

        Assert.Equal("1.0.0", distTags["latest"]!.GetValue<string>());
        Assert.DoesNotContain("next", distTags.Select(pair => pair.Key));
    }

    [Fact]
    public async Task GetPackumentAsync_EscapesEachSegmentOfAScopedNameInTheTarballUrl()
    {
        HavePackage(Package("@acme/left-pad", versions: Version(1, "1.0.0")));

        var packument = await _service.GetPackumentAsync("@acme%2fleft-pad", RegistryBaseUrl, Ct);

        Assert.Equal(
            "https://registry.test/npm/%40acme/left-pad/-/left-pad-1.0.0.tgz",
            packument!["versions"]!["1.0.0"]!["dist"]!["tarball"]!.GetValue<string>());
    }

    // ---------- single version ----------

    [Fact]
    public async Task GetVersionAsync_ReturnsNullForAnUnknownPackageOrSelector()
    {
        Assert.Null(await _service.GetVersionAsync("left-pad", "latest", RegistryBaseUrl, Ct));

        HavePackage(Package(versions: Version(1, "1.0.0")));

        Assert.Null(await _service.GetVersionAsync("left-pad", "9.9.9", RegistryBaseUrl, Ct));
    }

    [Fact]
    public async Task GetVersionAsync_ResolvesAnExactVersionAndStampsTheCanonicalFields()
    {
        HavePackage(Package(versions: [Version(1, "1.0.0"), Version(2, "2.0.0")]));

        var document = await _service.GetVersionAsync("left-pad", "2.0.0", RegistryBaseUrl, Ct);

        Assert.Equal("left-pad", document!["name"]!.GetValue<string>());
        Assert.Equal("2.0.0", document["version"]!.GetValue<string>());
        Assert.Equal("sha512-value", document["dist"]!["integrity"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetVersionAsync_ResolvesADistTagSelector()
    {
        HavePackage(Package(
            distTags: "{\"latest\":\"1.0.0\",\"next\":\"2.0.0\"}",
            versions: [Version(1, "1.0.0"), Version(2, "2.0.0")]));

        var document = await _service.GetVersionAsync("left-pad", "next", RegistryBaseUrl, Ct);

        Assert.Equal("2.0.0", document!["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetVersionAsync_RefusesADistTagResolvingToAnUnlistedVersion()
    {
        HavePackage(Package(
            distTags: "{\"latest\":\"2.0.0\"}",
            versions: [Version(1, "1.0.0"), Version(2, "2.0.0", listed: false)]));

        Assert.Null(await _service.GetVersionAsync("left-pad", "latest", RegistryBaseUrl, Ct));
    }

    // ---------- tarball ----------

    [Fact]
    public async Task GetTarballAsync_ReturnsNullWhenThePackageHasNoVersionListing()
    {
        Assert.Null(await _service.GetTarballAsync("left-pad", "left-pad-1.0.0.tgz", Ct));
    }

    [Fact]
    public async Task GetTarballAsync_ReturnsNullWhenTheFileNameMatchesNoVersion()
    {
        _repository.GetVersionNamesAsync(PackageRegistryKind.Npm, "left-pad", Arg.Any<CancellationToken>())
            .Returns(["1.0.0"]);

        Assert.Null(await _service.GetTarballAsync("left-pad", "left-pad-9.9.9.tgz", Ct));
    }

    [Fact]
    public async Task GetTarballAsync_ResolvesTheVersionBehindTheConventionalFileName()
    {
        _repository.GetVersionNamesAsync(PackageRegistryKind.Npm, "left-pad", Arg.Any<CancellationToken>())
            .Returns(["1.0.0", "2.0.0"]);
        _repository.GetVersionAsync(PackageRegistryKind.Npm, "left-pad", "2.0.0", Arg.Any<CancellationToken>())
            .Returns(Version(2, "2.0.0"));

        var version = await _service.GetTarballAsync("left-pad", "left-pad-2.0.0.tgz", Ct);

        Assert.Equal(2, version!.Id);
    }

    [Fact]
    public async Task GetTarballAsync_RefusesToServeAnUnlistedVersion()
    {
        _repository.GetVersionNamesAsync(PackageRegistryKind.Npm, "left-pad", Arg.Any<CancellationToken>())
            .Returns(["1.0.0"]);
        _repository.GetVersionAsync(PackageRegistryKind.Npm, "left-pad", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Version(1, "1.0.0", listed: false));

        Assert.Null(await _service.GetTarballAsync("left-pad", "left-pad-1.0.0.tgz", Ct));
    }

    [Fact]
    public async Task GetTarballAsync_UsesTheUnscopedBaseNameForAScopedPackage()
    {
        _repository.GetVersionNamesAsync(PackageRegistryKind.Npm, "@acme/left-pad", Arg.Any<CancellationToken>())
            .Returns(["1.0.0"]);
        _repository.GetVersionAsync(
                PackageRegistryKind.Npm, "@acme/left-pad", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Version(1, "1.0.0"));

        Assert.NotNull(await _service.GetTarballAsync("@acme%2Fleft-pad", "left-pad-1.0.0.tgz", Ct));
    }

    // ---------- search ----------

    [Fact]
    public async Task SearchAsync_ClampsTheWindowAndProjectsTheLatestVersionOfEachHit()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _repository.SearchAsync(
                PackageRegistryKind.Npm, "pad", 0, 1, true, Arg.Any<CancellationToken>())
            .Returns((
                [
                    new PackageRegistrySearchPackage(
                        "left-pad",
                        "pads on the left",
                        "{\"latest\":\"2.0.0\"}",
                        [
                            new PackageRegistrySearchVersion(1, "1.0.0", "1.0.0", false, true, older),
                            new PackageRegistrySearchVersion(2, "2.0.0", "2.0.0", false, true, older.AddDays(1))
                        ])
                ],
                7));

        var page = await _service.SearchAsync("pad", -5, 0, Ct);

        Assert.Equal(7, page.Total);
        var hit = Assert.Single(page.Packages);
        Assert.Equal("left-pad", hit.Name);
        Assert.Equal("2.0.0", hit.Version);
        Assert.Equal("pads on the left", hit.Description);
        Assert.Equal(older.AddDays(1), hit.PublishedAt);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToTheNewestListedVersionWithoutALatestTag()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _repository.SearchAsync(
                PackageRegistryKind.Npm, null, 0, 200, true, Arg.Any<CancellationToken>())
            .Returns((
                [
                    new PackageRegistrySearchPackage(
                        "left-pad",
                        null,
                        null,
                        [
                            new PackageRegistrySearchVersion(1, "1.0.0", "1.0.0", false, true, older),
                            new PackageRegistrySearchVersion(2, "3.0.0-beta", "3.0.0-beta", true, true, older.AddDays(2)),
                            new PackageRegistrySearchVersion(3, "2.0.0", "2.0.0", false, false, older.AddDays(3))
                        ])
                ],
                1));

        var hit = Assert.Single((await _service.SearchAsync(null, 0, 5000, Ct)).Packages);

        Assert.Equal("3.0.0-beta", hit.Version);
    }

    [Fact]
    public void OpenContent_DelegatesToTheStorageUsingTheStoredRelativePath()
    {
        var payload = new MemoryStream(Encoding.UTF8.GetBytes("tarball"));
        _storage.OpenRead("npm/left-pad/1.0.0.tgz").Returns(payload);

        Assert.Same(payload, _service.OpenContent(Version(1, "1.0.0")));
    }

    [Fact]
    public void OpenContent_ReturnsNullWhenTheStoredFileIsGone()
    {
        Assert.Null(_service.OpenContent(Version(1, "1.0.0")));
    }

    [Fact]
    public async Task GetVersionAsync_ThrowsWhenTheStoredMetadataIsNotAJsonObject()
    {
        HavePackage(Package(versions: Version(1, "1.0.0", metadata: "\"not-an-object\"")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.GetVersionAsync("left-pad", "1.0.0", RegistryBaseUrl, Ct));
    }

    [Fact]
    public async Task GetPackumentAsync_RejectsAnInvalidPackageName()
    {
        await Assert.ThrowsAnyAsync<Exception>(
            () => _service.GetPackumentAsync("../escape", RegistryBaseUrl, Ct));
    }
}
