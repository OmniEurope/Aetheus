// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

/// <summary>
/// The NuGet v3 endpoints are consumed by the real `dotnet` client, which follows the service index
/// to find every other route. A wrong resource type or a missing 404 does not fail loudly here: the
/// client silently falls back or hangs, so these pin the contract shapes the client depends on.
/// </summary>
public class NuGetRegistryControllerTests
{
    private readonly INuGetRegistryService _registry = Substitute.For<INuGetRegistryService>();

    private NuGetRegistryController Build() =>
        new(_registry, new PackageRegistryUploadGate())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static RegistryPackageVersion Version(string version = "1.2.3") => new()
    {
        Version = version,
        NormalizedVersion = version,
        IsListed = true
    };

    private static T Value<T>(IActionResult result) =>
        (T)Assert.IsType<OkObjectResult>(result).Value!;

    // --- service index ------------------------------------------------------

    [Fact]
    public void TheServiceIndexAdvertisesTheFourResourcesTheClientNeeds()
    {
        // dotnet restore reads this document and nothing else; a missing resource type makes the
        // feed look broken with no error of its own.
        var body = Value<object>(Build().GetServiceIndex());
        var json = System.Text.Json.JsonSerializer.Serialize(body);

        Assert.Contains("PackageBaseAddress/3.0.0", json, StringComparison.Ordinal);
        Assert.Contains("RegistrationsBaseUrl/3.6.0", json, StringComparison.Ordinal);
        Assert.Contains("SearchQueryService/3.5.0", json, StringComparison.Ordinal);
        Assert.Contains("PackagePublish/2.0.0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServiceIndexDeclaresTheV3ProtocolVersion()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Value<object>(Build().GetServiceIndex()));

        // Assert the version FIELD, not the substring: "PackageBaseAddress/3.0.0" already contains
        // "3.0.0", so a looser check would pass with the version property removed entirely.
        Assert.Contains("\"version\":\"3.0.0\"", json, StringComparison.Ordinal);
    }

    // --- versions -----------------------------------------------------------

    [Fact]
    public async Task AnUnknownPackageReturnsNotFound_RatherThanAnEmptyVersionList()
    {
        // An empty list would tell the client the package exists with no versions, which it caches.
        _registry.GetVersionsAsync("ghost", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<string>?)null);

        var result = await Build().GetVersions("ghost", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AKnownPackageReturnsItsVersions()
    {
        _registry.GetVersionsAsync("aetheus.shared", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "1.0.0", "1.1.0" });

        var result = await Build().GetVersions("aetheus.shared", TestContext.Current.CancellationToken);

        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Contains("1.0.0", json, StringComparison.Ordinal);
        Assert.Contains("1.1.0", json, StringComparison.Ordinal);
    }

    // --- download -----------------------------------------------------------

    [Fact]
    public async Task DownloadingAnUnknownVersionIsNotFound()
    {
        _registry.GetVersionAsync("pkg", "9.9.9", Arg.Any<CancellationToken>())
            .Returns((RegistryPackageVersion?)null);

        var result = await Build().Download("pkg", "9.9.9", "pkg.9.9.9.nupkg", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadingTheNupkgStreamsThePackageContent()
    {
        _registry.GetVersionAsync("pkg", "1.2.3", Arg.Any<CancellationToken>()).Returns(Version());
        _registry.OpenContent(Arg.Any<RegistryPackageVersion>()).Returns(new MemoryStream([1, 2, 3]));

        var result = await Build().Download("pkg", "1.2.3", "pkg.1.2.3.nupkg", TestContext.Current.CancellationToken);

        Assert.IsType<FileStreamResult>(result);
    }

    [Fact]
    public async Task DownloadingTheNuspecReturnsTheManifestAsXml()
    {
        // The client parses this as XML; serving it as JSON or octet-stream breaks restore.
        _registry.GetVersionAsync("pkg", "1.2.3", Arg.Any<CancellationToken>()).Returns(Version());
        _registry.ReadManifest(Arg.Any<RegistryPackageVersion>()).Returns("<package />");

        var result = await Build().Download("pkg", "1.2.3", "pkg.nuspec", TestContext.Current.CancellationToken);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/xml", content.ContentType);
        Assert.Equal("<package />", content.Content);
    }

    [Fact]
    public async Task RequestingAFileNameThatIsNeitherNupkgNorNuspecIsNotFound()
    {
        // Fail closed: the route captures an arbitrary file name from the URL.
        _registry.GetVersionAsync("pkg", "1.2.3", Arg.Any<CancellationToken>()).Returns(Version());

        var result = await Build().Download("pkg", "1.2.3", "secrets.txt", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task TheFileNameIsMatchedCaseInsensitively_AsTheClientSendsItLowercased()
    {
        _registry.GetVersionAsync("Pkg", "1.2.3", Arg.Any<CancellationToken>()).Returns(Version());
        _registry.ReadManifest(Arg.Any<RegistryPackageVersion>()).Returns("<package />");

        var result = await Build().Download("Pkg", "1.2.3", "PKG.NUSPEC", TestContext.Current.CancellationToken);

        Assert.IsType<ContentResult>(result);
    }

    [Fact]
    public async Task WhenTheContentIsMissingOnDisk_TheDownloadIsNotFound_NotAnEmptyFile()
    {
        // An empty stream would install a corrupt package rather than failing the restore.
        _registry.GetVersionAsync("pkg", "1.2.3", Arg.Any<CancellationToken>()).Returns(Version());
        _registry.OpenContent(Arg.Any<RegistryPackageVersion>()).Returns((Stream?)null);

        var result = await Build().Download("pkg", "1.2.3", "pkg.1.2.3.nupkg", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    // --- registration -------------------------------------------------------

    [Fact]
    public async Task AnUnknownRegistrationIsNotFound()
    {
        _registry.GetRegistrationAsync("ghost", Arg.Any<CancellationToken>()).Returns((NuGetRegistration?)null);

        var result = await Build().GetRegistration("ghost", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AnUnknownRegistrationLeafIsNotFound()
    {
        _registry.GetRegistrationVersionAsync("ghost", "1.0.0", Arg.Any<CancellationToken>())
            .Returns((NuGetRegistration?)null);

        var result = await Build().GetRegistrationLeaf("ghost", "1.0.0", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARegistrationLeaf_CarriesTheDeprecationEntryOnlyForADeprecatedVersion(bool deprecated)
    {
        // R1-18: the NuGet client reads catalogEntry.deprecation to warn on restore.
        _registry.GetRegistrationVersionAsync("pkg", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(new NuGetRegistration("Pkg", null, null, null,
                [new NuGetRegistrationVersion("1.0.0", true, new DateTime(2026, 1, 1), [], deprecated)]));

        var result = await Build().GetRegistrationLeaf("pkg", "1.0.0", TestContext.Current.CancellationToken);

        var catalogEntry = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(Value<object>(result)))!["catalogEntry"]!;
        var deprecation = catalogEntry["deprecation"];
        if (!deprecated)
        {
            Assert.Null(deprecation);
            return;
        }

        Assert.Equal("Other", deprecation!["reasons"]![0]!.GetValue<string>());
        Assert.Equal(Aetheus.Back.Components.PackageRegistry.PackageRegistryDefaults.DeprecationMessage, deprecation["message"]!.GetValue<string>());
    }

    // --- search -------------------------------------------------------------

    [Fact]
    public async Task SearchReportsTheTotalHits_NotJustThePageSize()
    {
        // The client pages through results using totalHits; returning the page size stops it early.
        _registry.SearchAsync(null, 0, 20, false, Arg.Any<CancellationToken>())
            .Returns(new NuGetSearchPage(137, []));

        var result = await Build().Search(null, 0, 20, false, TestContext.Current.CancellationToken);

        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Contains("137", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPassesThePrereleaseFlagThrough()
    {
        // Ignoring it would serve prerelease packages to a restore that excluded them.
        _registry.SearchAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new NuGetSearchPage(0, []));

        await Build().Search("json", 0, 20, true, TestContext.Current.CancellationToken);

        await _registry.Received().SearchAsync("json", 0, 20, true, Arg.Any<CancellationToken>());
    }

    // --- publish, unlist, relist ---------------------------------------------

    [Fact]
    public async Task APublishWhileAnotherIsRunning_IsRefusedWith429()
    {
        var gate = new PackageRegistryUploadGate();
        using var held = gate.TryEnter();
        var sut = new NuGetRegistryController(_registry, gate)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await sut.Publish(TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<StatusCodeResult>(result).StatusCode);
        await _registry.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APublishThatIsNotMultipart_IsABadRequest()
    {
        var sut = Build();
        sut.ControllerContext.HttpContext.Request.ContentType = "application/octet-stream";

        var result = await sut.Publish(TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task APublishWithAnEmptyPackage_IsABadRequest()
    {
        var sut = BuildWithForm(new FormFile(Stream.Null, 0, 0, "package", "empty.nupkg"));

        var result = await sut.Publish(TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        await _registry.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APublishedPackage_IsStoredUnderThePublisherName_AndAnswers201()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var sut = BuildWithForm(new FormFile(new MemoryStream(bytes), 0, bytes.Length, "package", "a.nupkg"));
        sut.ControllerContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(
                System.Security.Claims.ClaimTypes.Name, "alice")], "test"));
        byte[]? published = null;
        await _registry.PublishAsync(Arg.Do<Func<Stream>>(open =>
        {
            using var copy = new MemoryStream();
            open().CopyTo(copy);
            published = copy.ToArray();
        }), "alice", Arg.Any<CancellationToken>());

        var result = await sut.Publish(TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(bytes, published);
    }

    [Theory]
    [InlineData(true, typeof(NoContentResult))]
    [InlineData(false, typeof(NotFoundResult))]
    public async Task UnlistAndRelist_AnswerWhetherTheVersionChanged(bool changed, Type expected)
    {
        _registry.SetListedAsync("pkg", "1.0.0", Arg.Any<bool>(), "unknown", Arg.Any<CancellationToken>())
            .Returns(changed);

        var unlist = await Build().Unlist("pkg", "1.0.0", TestContext.Current.CancellationToken);
        var relist = await Build().Relist("pkg", "1.0.0", TestContext.Current.CancellationToken);

        Assert.IsType(expected, unlist);
        Assert.IsType(expected, relist);
        await _registry.Received(1).SetListedAsync("pkg", "1.0.0", false, "unknown", Arg.Any<CancellationToken>());
        await _registry.Received(1).SetListedAsync("pkg", "1.0.0", true, "unknown", Arg.Any<CancellationToken>());
    }

    private NuGetRegistryController BuildWithForm(IFormFile file)
    {
        var sut = Build();
        var request = sut.ControllerContext.HttpContext.Request;
        request.ContentType = "multipart/form-data; boundary=test";
        request.Form = new FormCollection([], new FormFileCollection { file });
        return sut;
    }
}
