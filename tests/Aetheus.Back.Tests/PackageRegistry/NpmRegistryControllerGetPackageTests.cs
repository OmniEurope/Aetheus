// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

/// <summary>The npm route answers a tarball, a packument or one version, scoped names included, and a
/// 404 for anything the registry does not hold.</summary>
public sealed class NpmRegistryControllerGetPackageTests
{
    private const string BaseUrl = "https://feeds.example/api/packages/npm";
    private readonly INpmRegistryService _registry = Substitute.For<INpmRegistryService>();
    private readonly NpmRegistryController _sut;

    public NpmRegistryControllerGetPackageTests()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("feeds.example");
        _sut = new NpmRegistryController(_registry, new PackageRegistryUploadGate(), TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    [Theory]
    [InlineData("left-pad", "left-pad")]
    [InlineData("@acme%2fui", "@acme/ui")]
    [InlineData("/@acme/ui/", "@acme/ui")]
    public async Task APackageName_AnswersItsPackument(string path, string name)
    {
        var packument = new JsonObject { ["name"] = name };
        _registry.GetPackumentAsync(name, BaseUrl, Arg.Any<CancellationToken>()).Returns(packument);

        var result = await _sut.GetPackage(path, TestContext.Current.CancellationToken);

        Assert.Same(packument, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Theory]
    [InlineData("left-pad/1.3.0", "left-pad", "1.3.0")]
    [InlineData("@acme/ui/latest", "@acme/ui", "latest")]
    public async Task ANameAndASelector_AnswerThatVersion(string path, string name, string selector)
    {
        var version = new JsonObject { ["version"] = selector };
        _registry.GetVersionAsync(name, selector, BaseUrl, Arg.Any<CancellationToken>()).Returns(version);

        var result = await _sut.GetPackage(path, TestContext.Current.CancellationToken);

        Assert.Same(version, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task ATarballPath_StreamsTheStoredContent()
    {
        var version = new RegistryPackageVersion();
        _registry.GetTarballAsync("@acme/ui", "ui-1.0.0.tgz", Arg.Any<CancellationToken>()).Returns(version);
        _registry.OpenContent(version).Returns(new MemoryStream([1, 2, 3]));

        var result = await _sut.GetPackage("@acme/ui/-/ui-1.0.0.tgz", TestContext.Current.CancellationToken);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal(Aetheus.Back.Components.PackageRegistry.PackageRegistryDefaults.NpmContentType, file.ContentType);
        Assert.True(file.EnableRangeProcessing);
    }

    [Fact]
    public async Task AnUnknownTarballOrAMissingFile_IsNotFound()
    {
        var version = new RegistryPackageVersion();
        _registry.GetTarballAsync("ui", "ui-2.0.0.tgz", Arg.Any<CancellationToken>()).Returns(version);
        _registry.OpenContent(version).Returns((Stream?)null);

        Assert.IsType<NotFoundResult>(await _sut.GetPackage("ui/-/ui-1.0.0.tgz", TestContext.Current.CancellationToken));
        Assert.IsType<NotFoundResult>(await _sut.GetPackage("ui/-/ui-2.0.0.tgz", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnknownPackageOrVersion_IsNotFound()
    {
        Assert.IsType<NotFoundResult>(await _sut.GetPackage("missing", TestContext.Current.CancellationToken));
        Assert.IsType<NotFoundResult>(await _sut.GetPackage("missing/1.0.0", TestContext.Current.CancellationToken));
    }
}
