// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Net;
using System.Text;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-31: the license of a NuGet target version, read from the registry's own metadata (registration leaf, then the catalog entry
/// it names), against a scripted HTTP handler. No real registry is called here: what is proved is the requests the reader makes (two GETs
/// on the registry's host, the registration answer decompressed as nuget.org serves it), what it reads, and every refusal. Whether nuget.org
/// still answers this way was only checked by hand against one package (Newtonsoft.Json 13.0.3), not by these tests.
/// </summary>
public sealed class NuGetLicenseReaderTests
{
    private const string Catalog = "https://api.nuget.org/v3/catalog0/data/2023.03.08.07.46.17/newtonsoft.json.13.0.3.json";

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static NuGetRegistryLicenseReader Reader(ScriptedHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(NuGetRegistryLicenseReader.HttpClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new NuGetRegistryLicenseReader(factory, NullLogger<NuGetRegistryLicenseReader>.Instance);
    }

    private static HttpResponseMessage Json(string json, bool gzip = false)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (gzip)
        {
            using var output = new MemoryStream();
            using (var compressor = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) compressor.Write(bytes);
            bytes = output.ToArray();
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    private static HttpResponseMessage Leaf(string catalogEntry = Catalog) =>
        Json($$"""{"@id":"x","catalogEntry":"{{catalogEntry}}","listed":true}""", gzip: true);

    private static ScriptedHandler Registry(string catalogJson, string catalogEntry = Catalog) =>
        new(request => request.RequestUri!.AbsolutePath.Contains("/registration5-gz-semver2/", StringComparison.Ordinal)
            ? Leaf(catalogEntry)
            : Json(catalogJson));

    [Fact]
    public async Task Read_ReadsTheLicenseExpressionOfTheExactVersion_FromTheRegistrationLeafThenTheCatalogEntry()
    {
        var handler = Registry("""{"licenseExpression":"MIT","licenseUrl":"https://licenses.nuget.org/MIT"}""");

        var answer = await Reader(handler).ReadAsync("Newtonsoft.Json", "13.0.3", TestContext.Current.CancellationToken);

        Assert.Equal("MIT", answer.Expression);
        Assert.Null(answer.Failure);
        Assert.Equal(
            ["https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/13.0.3.json", Catalog],
            handler.Requests.Select(url => url.AbsoluteUri));
    }

    [Fact]
    public async Task Read_VersionWithBuildMetadata_IsAskedByItsNormalizedLowerCaseForm()
    {
        var handler = Registry("""{"licenseExpression":"Apache-2.0"}""");

        await Reader(handler).ReadAsync("Some.Package", "1.0.0-Beta.1+Build5", TestContext.Current.CancellationToken);

        Assert.Equal("https://api.nuget.org/v3/registration5-gz-semver2/some.package/1.0.0-beta.1.json", handler.Requests[0].AbsoluteUri);
    }

    // nuget.org names a version by its normalized form only (checked on serilog: 4.0.0.json answers, 4.0.json is a 404).
    [Theory]
    [InlineData("4.0", "4.0.0")]
    [InlineData("4", "4.0.0")]
    [InlineData("4.0.0.0", "4.0.0")]
    [InlineData("4.0.0.1", "4.0.0.1")]
    [InlineData("04.01.002", "4.1.2")]
    [InlineData("1.0-RC.1", "1.0.0-rc.1")]
    public async Task Read_VersionNotInItsNormalizedForm_IsAskedByTheNormalizedOne(string version, string normalized)
    {
        var handler = Registry("""{"licenseExpression":"Apache-2.0"}""");

        await Reader(handler).ReadAsync("Serilog", version, TestContext.Current.CancellationToken);

        Assert.Equal($"https://api.nuget.org/v3/registration5-gz-semver2/serilog/{normalized}.json", handler.Requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task Read_NoLicenseExpression_IsAFailureThatSaysSo_NotALicenseReadFromTheLegacyUrl()
    {
        var handler = Registry("""{"licenseExpression":"","licenseUrl":"https://example.com/license.txt"}""");

        var answer = await Reader(handler).ReadAsync("Old.Package", "1.2.3", TestContext.Current.CancellationToken);

        Assert.Null(answer.Expression);
        Assert.Contains("declares no license expression", answer.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_UnknownVersion_IsAFailure()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var answer = await Reader(handler).ReadAsync("Private.Package", "9.9.9", TestContext.Current.CancellationToken);

        Assert.Null(answer.Expression);
        Assert.Contains("has no version 9.9.9", answer.Failure, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://api.nuget.org/v3/catalog0/data/x.json")]
    [InlineData("https://evil.example.com/v3/catalog0/data/x.json")]
    [InlineData("https://api.nuget.org/v3/other/x.json")]
    [InlineData("https://api.nuget.org:8443/v3/catalog0/data/x.json")]
    [InlineData("https://api.nuget.org/v3/catalog0/data/../../index.json")]
    public async Task Read_CatalogEntryOutsideTheRegistrysCatalog_IsNeverFollowed(string entry)
    {
        var handler = Registry("""{"licenseExpression":"MIT"}""", entry);

        var answer = await Reader(handler).ReadAsync("Newtonsoft.Json", "13.0.3", TestContext.Current.CancellationToken);

        Assert.Null(answer.Expression);
        Assert.Contains("no catalog entry", answer.Failure, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Read_RegistryThatDoesNotAnswer_IsAFailureWithItsReason()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("boom"));

        var answer = await Reader(handler).ReadAsync("Newtonsoft.Json", "13.0.3", TestContext.Current.CancellationToken);

        Assert.Null(answer.Expression);
        Assert.Contains("did not answer", answer.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_ServerError_IsAFailureNotAnEmptyLicense()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var answer = await Reader(handler).ReadAsync("Newtonsoft.Json", "13.0.3", TestContext.Current.CancellationToken);

        Assert.Null(answer.Expression);
        Assert.Contains("503", answer.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../evil", "1.0.0")]
    [InlineData("Newtonsoft.Json", "1.0.0/../x")]
    [InlineData("", "1.0.0")]
    [InlineData("Newtonsoft.Json", "latest")]
    public async Task Read_NameOrVersionThatIsNotNuGets_IsRefusedWithoutAnyRequest(string id, string version)
    {
        var handler = new ScriptedHandler(_ => Json("{}"));

        var answer = await Reader(handler).ReadAsync(id, version, TestContext.Current.CancellationToken);

        Assert.Null(answer.Expression);
        Assert.NotNull(answer.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Read_CompressedAnswerThatInflatesPastTheBound_IsAFailure()
    {
        var bomb = new string(' ', 3 * 1024 * 1024);
        var handler = new ScriptedHandler(_ => Json($$"""{"catalogEntry":"{{Catalog}}","pad":"{{bomb}}"}""", gzip: true));

        var answer = await Reader(handler).ReadAsync("Newtonsoft.Json", "13.0.3", TestContext.Current.CancellationToken);

        Assert.Null(answer.Expression);
        Assert.Contains("did not answer", answer.Failure, StringComparison.Ordinal);
    }
}
