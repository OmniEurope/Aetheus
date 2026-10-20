// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.PackageRegistry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

/// <summary>
/// Maven's resolver reads any non-empty 404 body as a transfer failure, and deploys with a bare PUT, so
/// these pin the status codes and bodies the Maven client depends on.
/// </summary>
public sealed class MavenRegistryControllerTests
{
    private readonly IMavenRegistryService _registry = Substitute.For<IMavenRegistryService>();

    private MavenRegistryController Build(string body = "") => new(_registry, new PackageRegistryUploadGate())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Request = { Body = new MemoryStream(Encoding.UTF8.GetBytes(body)) } }
        }
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("org/example")]
    [InlineData("org/example/../demo/1.0/demo-1.0.jar")]
    [InlineData("org/example/demo/1.0/other-1.0.jar")]
    public async Task Get_AnInvalidPath_IsABareNotFound_NeverAProblemDocument(string path)
    {
        var result = await Build().Get(path, Ct);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, content.StatusCode);
        Assert.Null(content.Content);
        await _registry.DidNotReceiveWithAnyArgs().GetAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Get_NothingStoredAtThePath_IsABareNotFound()
    {
        _registry.GetAsync(Arg.Any<MavenRepositoryPath>(), Arg.Any<CancellationToken>())
            .Returns((MavenRepositoryContent?)null);

        var result = await Build().Get("org/example/demo/1.0/demo-1.0.jar", Ct);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ContentResult>(result).StatusCode);
    }

    [Fact]
    public async Task Get_GeneratedContent_IsReturnedFromMemoryWithItsMediaType()
    {
        _registry.GetAsync(Arg.Any<MavenRepositoryPath>(), Arg.Any<CancellationToken>())
            .Returns(new MavenRepositoryContent("application/xml", [1, 2, 3], null));

        var result = await Build().Get("org/example/demo/maven-metadata.xml", Ct);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/xml", file.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.FileContents);
    }

    [Fact]
    public async Task Get_StoredFile_IsStreamedWithRangeSupport()
    {
        _registry.GetAsync(Arg.Any<MavenRepositoryPath>(), Arg.Any<CancellationToken>())
            .Returns(new MavenRepositoryContent("application/java-archive", null, "maven/x/demo.jar"));
        _registry.OpenStored("maven/x/demo.jar").Returns(new MemoryStream([9]));

        var result = await Build().Get("org/example/demo/1.0/demo-1.0.jar", Ct);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.True(file.EnableRangeProcessing);
        Assert.Equal("application/java-archive", file.ContentType);
    }

    [Fact]
    public async Task Get_StoredFileMissingOnDisk_IsABareNotFound()
    {
        _registry.GetAsync(Arg.Any<MavenRepositoryPath>(), Arg.Any<CancellationToken>())
            .Returns(new MavenRepositoryContent("application/java-archive", null, "maven/x/demo.jar"));
        _registry.OpenStored("maven/x/demo.jar").Returns((Stream?)null);

        var result = await Build().Get("org/example/demo/1.0/demo-1.0.jar", Ct);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ContentResult>(result).StatusCode);
    }

    [Fact]
    public async Task Put_AnInvalidPath_IsBadRequestAndStoresNothing()
    {
        var result = await Build("x").Put("org/example/demo/1.0/other-1.0.jar", Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        await _registry.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Put_AValidPath_StoresTheBodyAndAnswersCreated()
    {
        var result = await Build("jar-bytes").Put("org/example/demo/1.0/demo-1.0.jar", Ct);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<StatusCodeResult>(result).StatusCode);
        await _registry.Received(1).PutAsync(
            Arg.Is<MavenRepositoryPath>(path => path.Coordinates == "org.example:demo" && path.Version == "1.0"),
            Arg.Any<Stream>(), "unknown", Arg.Any<CancellationToken>());
    }
}
