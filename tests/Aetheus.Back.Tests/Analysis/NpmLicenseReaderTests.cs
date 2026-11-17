// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-8: the license of an npm target version, read from the registry's manifest of that exact version, against a scripted
/// HTTP handler. No real registry is called: what is proved is the request the reader makes, each shape of the <c>license</c> field it
/// reads (today's SPDX string, the legacy object and arrays) and every refusal. Whether registry.npmjs.org still answers this way is
/// not proved by these tests.
/// </summary>
public sealed class NpmLicenseReaderTests
{
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static NpmRegistryLicenseReader Reader(ScriptedHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(NpmRegistryLicenseReader.HttpClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new NpmRegistryLicenseReader(factory, NullLogger<NpmRegistryLicenseReader>.Instance);
    }

    private static ScriptedHandler Manifest(string json) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    private static Task<RegistryLicenseAnswer> Read(ScriptedHandler handler, string name = "lodash", string version = "4.17.21") =>
        Reader(handler).ReadAsync(name, version, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Read_SpdxString_IsTheLicenseOfTheExactVersionAsked()
    {
        var handler = Manifest("""{"name":"lodash","version":"4.17.21","license":"MIT"}""");

        var answer = await Read(handler);

        Assert.Equal(("MIT", (string?)null), (answer.Expression, answer.Failure));
        Assert.Equal(["https://registry.npmjs.org/lodash/4.17.21"], handler.Requests.Select(url => url.AbsoluteUri));
    }

    [Fact]
    public async Task Read_ScopedPackage_KeepsItsScopePath()
    {
        var handler = Manifest("""{"license":"(MIT OR Apache-2.0)"}""");

        var answer = await Read(handler, "@scope/pkg", "1.2.4");

        Assert.Equal("(MIT OR Apache-2.0)", answer.Expression);
        Assert.Equal("https://registry.npmjs.org/@scope/pkg/1.2.4", handler.Requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task Read_LegacyObject_IsReadByItsType()
    {
        var answer = await Read(Manifest("""{"license":{"type":"ISC","url":"https://opensource.org/licenses/ISC"}}"""));

        Assert.Equal("ISC", answer.Expression);
    }

    [Fact]
    public async Task Read_LegacyLicensesArray_IsReadAsAllOfThemApplying()
    {
        var answer = await Read(Manifest("""{"licenses":[{"type":"MIT","url":"x"},{"type":"Apache-2.0","url":"y"}]}"""));

        Assert.Equal("(MIT) AND (Apache-2.0)", answer.Expression);
    }

    [Fact]
    public async Task Read_LegacyArrayInTheLicenseField_OfStringsAndObjects_IsRead()
    {
        var answer = await Read(Manifest("""{"license":["BSD-3-Clause",{"type":"MIT"}]}"""));

        Assert.Equal("(BSD-3-Clause) AND (MIT)", answer.Expression);
    }

    [Fact]
    public async Task Read_LegacyArrayWithOneEntryThatIsNotAnExpression_IsRefused()
    {
        var answer = await Read(Manifest("""{"licenses":[{"type":"MIT"},{"url":"https://example.com/license"}]}"""));

        Assert.Null(answer.Expression);
        Assert.Contains("declares no license expression", answer.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SEE LICENSE IN LICENSE.md")]
    [InlineData("Proprietary, all rights reserved")]
    [InlineData("MIT OR")]
    public async Task Read_TextThatIsNotAnSpdxExpression_IsRefusedWithWhatTheRegistrySaid(string declared)
    {
        var answer = await Read(Manifest($$"""{"license":"{{declared}}"}"""));

        Assert.Null(answer.Expression);
        Assert.Equal($"The npm registry declares '{declared}' for lodash 4.17.21, which is not an SPDX license expression.", answer.Failure);
    }

    [Theory]
    [InlineData("""{"name":"lodash"}""")]
    [InlineData("""{"license":""}""")]
    [InlineData("""{"license":null}""")]
    [InlineData("""{"licenses":[]}""")]
    public async Task Read_NoLicense_IsAFailureThatSaysSo(string manifest)
    {
        var answer = await Read(Manifest(manifest));

        Assert.Null(answer.Expression);
        Assert.Equal("The npm registry declares no license expression for lodash 4.17.21.", answer.Failure);
    }

    [Fact]
    public async Task Read_LicenseOfAShapeNotRead_IsRefused()
    {
        var answer = await Read(Manifest("""{"license":42}"""));

        Assert.Null(answer.Expression);
        Assert.Contains("shape that is not read", answer.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_UnknownVersion_IsAFailure()
    {
        var answer = await Read(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), version: "9.9.9");

        Assert.Null(answer.Expression);
        Assert.Equal("The npm registry has no version 9.9.9 of lodash.", answer.Failure);
    }

    [Fact]
    public async Task Read_ServerError_IsAFailureNotAnEmptyLicense()
    {
        var answer = await Read(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        Assert.Null(answer.Expression);
        Assert.Equal("The npm registry answered 503 for lodash 4.17.21.", answer.Failure);
    }

    [Fact]
    public async Task Read_Timeout_IsAFailureWithItsReason()
    {
        // HttpClient reports its own timeout as a TaskCanceledException while the caller's token is not cancelled.
        var answer = await Read(new ScriptedHandler(_ => throw new TaskCanceledException("timeout")));

        Assert.Null(answer.Expression);
        Assert.Equal("The npm registry did not answer for lodash 4.17.21 (TaskCanceledException).", answer.Failure);
    }

    [Fact]
    public async Task Read_UnreadableBody_IsAFailure()
    {
        var answer = await Read(Manifest("<html>not json</html>"));

        Assert.Null(answer.Expression);
        Assert.Contains("did not answer", answer.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_CallerCancellation_IsNotSwallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = Manifest("""{"license":"MIT"}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(handler).ReadAsync("lodash", "4.17.21", cancelled.Token));
    }

    [Theory]
    [InlineData("../evil", "1.0.0")]
    [InlineData("..", "1.0.0")]
    [InlineData("lodash", "1.0.0/../x")]
    [InlineData("", "1.0.0")]
    [InlineData("lodash", "latest")]
    [InlineData("lodash", "^4.0.0")]
    public async Task Read_NameOrVersionThatIsNotNpms_IsRefusedWithoutAnyRequest(string name, string version)
    {
        var handler = Manifest("""{"license":"MIT"}""");

        var answer = await Read(handler, name, version);

        Assert.Null(answer.Expression);
        Assert.NotNull(answer.Failure);
        Assert.Empty(handler.Requests);
    }
}
