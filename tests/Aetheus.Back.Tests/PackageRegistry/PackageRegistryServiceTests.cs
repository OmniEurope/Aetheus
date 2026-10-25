// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

public class PackageRegistryServiceTests
{
    [Fact]
    public async Task NpmPublish_PreservesExistingDistributionTags()
    {
        var repository = Substitute.For<IPackageRegistryRepository>();
        var storage = Substitute.For<IPackageRegistryStorage>();
        var audit = Substitute.For<IAuditService>();
        var notifier = Substitute.For<IAdminChangeNotifier>();
        var package = new RegistryPackage
        {
            Id = 7,
            Kind = PackageRegistryKind.Npm,
            Name = "@aetheus/test",
            NormalizedName = "@aetheus/test",
            DistTagsJson = "{\"beta\":\"1.0.0\"}",
            Versions =
            [
                new RegistryPackageVersion
                {
                    Id = 1,
                    Version = "1.0.0",
                    NormalizedVersion = "1.0.0",
                    IsListed = true
                }
            ]
        };
        repository.GetPackageForUpdateAsync(
                PackageRegistryKind.Npm, "@aetheus/test", Arg.Any<CancellationToken>())
            .Returns(package);
        storage.SaveAsync(
                PackageRegistryKind.Npm, "@aetheus/test", "1.1.0", ".tgz",
                Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new StoredPackagePayload("npm/test/1.1.0/package.tgz", 10, new string('a', 64),
                new string('b', 40), "sha512-test"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new NpmRegistryService(
            repository, storage, new NpmPackageInspector(), new PackageRegistryPublishGate(), audit, configuration, notifier);
        using var document = CreateNpmPublishDocument("@aetheus/test", "1.1.0");

        await service.PublishAsync(
            "@aetheus/test", document, "admin", TestContext.Current.CancellationToken);

        using var tags = JsonDocument.Parse(package.DistTagsJson!);
        Assert.Equal("1.0.0", tags.RootElement.GetProperty("beta").GetString());
        Assert.Equal("1.1.0", tags.RootElement.GetProperty("latest").GetString());
        await notifier.Received(1).BroadcastAsync(
            AdminEntities.PackageRegistry,
            package.Id,
            EntityChangeOps.Updated,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdminSetListed_BroadcastsRegistryChange()
    {
        var repository = Substitute.For<IPackageRegistryRepository>();
        var audit = Substitute.For<IAuditService>();
        var notifier = Substitute.For<IAdminChangeNotifier>();
        var package = new RegistryPackage
        {
            Id = 7,
            Kind = PackageRegistryKind.NuGet,
            Name = "Sample",
            NormalizedName = "sample",
            Versions = [new RegistryPackageVersion { Id = 11, Version = "1.0.0", IsListed = true }]
        };
        repository.GetPackageByIdForUpdateAsync(7, Arg.Any<CancellationToken>()).Returns(package);
        var service = new PackageRegistryAdminService(repository, audit, notifier);

        var changed = await service.SetListedAsync(
            7, 11, false, TestContext.Current.CancellationToken);

        Assert.True(changed);
        Assert.False(package.Versions.Single().IsListed);
        await notifier.Received(1).BroadcastAsync(
            AdminEntities.PackageRegistry,
            7,
            EntityChangeOps.Updated,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, "Deprecated")]
    [InlineData(false, "Undeprecated")]
    public async Task SetDeprecatedAsync_FlagsOnlyThatVersion_SavesAuditsAndNotifies(bool deprecated, string action)
    {
        var repository = Substitute.For<IPackageRegistryRepository>();
        var audit = Substitute.For<IAuditService>();
        var notifier = Substitute.For<IAdminChangeNotifier>();
        var package = new RegistryPackage
        {
            Id = 7,
            Kind = PackageRegistryKind.Npm,
            Name = "Sample",
            NormalizedName = "sample",
            Versions =
            [
                new RegistryPackageVersion { Id = 11, Version = "1.0.0", IsDeprecated = !deprecated },
                new RegistryPackageVersion { Id = 12, Version = "2.0.0" }
            ]
        };
        repository.GetPackageByIdForUpdateAsync(7, Arg.Any<CancellationToken>()).Returns(package);
        var service = new PackageRegistryAdminService(repository, audit, notifier);

        var changed = await service.SetDeprecatedAsync(7, 11, deprecated, TestContext.Current.CancellationToken);

        Assert.True(changed);
        Assert.Equal(deprecated, package.Versions.Single(version => version.Id == 11).IsDeprecated);
        Assert.False(package.Versions.Single(version => version.Id == 12).IsDeprecated);
        await repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await audit.Received(1).LogAsync(
            action, "NpmPackage", 7, "Sample@1.0.0", Arg.Any<CancellationToken>());
        await notifier.Received(1).BroadcastAsync(
            AdminEntities.PackageRegistry, 7, EntityChangeOps.Updated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDeprecatedAsync_ReturnsFalseForAnUnknownVersion_WithoutSaving()
    {
        var repository = Substitute.For<IPackageRegistryRepository>();
        repository.GetPackageByIdForUpdateAsync(7, Arg.Any<CancellationToken>())
            .Returns(new RegistryPackage { Id = 7, Versions = [] });
        var service = new PackageRegistryAdminService(
            repository, Substitute.For<IAuditService>(), Substitute.For<IAdminChangeNotifier>());

        var changed = await service.SetDeprecatedAsync(7, 99, true, TestContext.Current.CancellationToken);

        Assert.False(changed);
        await repository.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NpmPublish_DatabaseFailureRemovesNewlyPromotedPayload()
    {
        var repository = Substitute.For<IPackageRegistryRepository>();
        var storage = Substitute.For<IPackageRegistryStorage>();
        var audit = Substitute.For<IAuditService>();
        var notifier = Substitute.For<IAdminChangeNotifier>();
        repository.GetPackageForUpdateAsync(
                PackageRegistryKind.Npm, "@aetheus/test", Arg.Any<CancellationToken>())
            .Returns((RegistryPackage?)null);
        storage.SaveAsync(
                PackageRegistryKind.Npm, "@aetheus/test", "1.1.0", ".tgz",
                Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new StoredPackagePayload(
                "npm/test/1.1.0/package.tgz", 10, new string('a', 64),
                new string('b', 40), "sha512-test", CreatedNew: true));
        repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("database unavailable"));
        var service = new NpmRegistryService(
            repository,
            storage,
            new NpmPackageInspector(),
            new PackageRegistryPublishGate(),
            audit,
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            notifier);
        using var document = CreateNpmPublishDocument("@aetheus/test", "1.1.0");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(
            "@aetheus/test", document, "admin", TestContext.Current.CancellationToken));

        await storage.Received(1).DeleteAsync(
            "npm/test/1.1.0/package.tgz",
            CancellationToken.None);
    }

    [Fact]
    public async Task NpmPublish_MalformedJson_ReturnsBadRequest()
    {
        var service = Substitute.For<INpmRegistryService>();
        var controller = new NpmRegistryController(
            service,
            new PackageRegistryUploadGate(),
            new FakeTimeProvider())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Body = new MemoryStream("{"u8.ToArray());

        var result = await controller.Publish("sample", TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        await service.DidNotReceiveWithAnyArgs().PublishAsync(
            default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NuGetRegistrationLeaf_ReturnsAdvertisedPackageDetails()
    {
        var service = Substitute.For<INuGetRegistryService>();
        service.GetRegistrationVersionAsync("sample", "1.2.3", Arg.Any<CancellationToken>())
            .Returns(new NuGetRegistration(
                "Sample", "Description", "Aetheus", "internal",
                [new NuGetRegistrationVersion("1.2.3", true, new DateTime(2026, 1, 1), [])]));
        var controller = new NuGetRegistryController(service, new PackageRegistryUploadGate())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("aetheus.example");

        var result = await controller.GetRegistrationLeaf(
            "sample", "1.2.3", TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var leaf = Assert.IsType<JsonObject>(ok.Value);
        Assert.Equal("1.2.3", leaf["catalogEntry"]?["version"]?.GetValue<string>());
        Assert.Equal("https://aetheus.example/api/packages/nuget/v3/flatcontainer/sample/1.2.3/sample.1.2.3.nupkg",
            leaf["packageContent"]?.GetValue<string>());
    }

    [Fact]
    public void UploadGate_RejectsConcurrentParserWithoutQueueing()
    {
        var gate = new PackageRegistryUploadGate();
        using var first = gate.TryEnter();

        Assert.NotNull(first);
        Assert.Null(gate.TryEnter());
    }

    private static JsonDocument CreateNpmPublishDocument(string name, string version)
    {
        var tarball = CreateNpmTarball(name, version);
        var root = new JsonObject
        {
            ["name"] = name,
            ["versions"] = new JsonObject
            {
                [version] = new JsonObject { ["name"] = name, ["version"] = version }
            },
            ["dist-tags"] = new JsonObject { ["latest"] = version },
            ["_attachments"] = new JsonObject
            {
                ["package.tgz"] = new JsonObject { ["data"] = Convert.ToBase64String(tarball) }
            }
        };
        return JsonDocument.Parse(root.ToJsonString());
    }

    private static byte[] CreateNpmTarball(string name, string version)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            var json = new MemoryStream(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { name, version })));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/package.json")
            {
                DataStream = json
            });
        }
        return output.ToArray();
    }
}
