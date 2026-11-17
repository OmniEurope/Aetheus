// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-8: the license of a Packagist target version, read from the registry's p2 metadata, against a scripted HTTP handler. No
/// real registry is called: what is proved is the request the reader makes, the version entry it picks (the minified format expanded
/// as Composer does), the reading of the <c>license</c> array (several entries = a choice, <c>OR</c>) and every refusal. Whether
/// repo.packagist.org still answers this way is not proved by these tests.
/// </summary>
public sealed class PackagistLicenseReaderTests
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

    private static PackagistRegistryLicenseReader Reader(ScriptedHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(PackagistRegistryLicenseReader.HttpClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new PackagistRegistryLicenseReader(factory, NullLogger<PackagistRegistryLicenseReader>.Instance);
    }

    private const string MinifiedMarker = ",\"minified\":\"composer/2.0\"";

    private static ScriptedHandler Metadata(string versionsJson, bool minified = true)
    {
        var marker = minified ? MinifiedMarker : string.Empty;
        return new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"packages":{"monolog/monolog":{{versionsJson}}}{{marker}}}""", Encoding.UTF8, "application/json")
        });
    }

    private static Task<RegistryLicenseAnswer> Read(ScriptedHandler handler, string name = "monolog/monolog", string version = "3.5.1") =>
        Reader(handler).ReadAsync(name, version, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Read_LicenseOfTheExactVersion_IsRead()
    {
        var handler = Metadata("""[{"version":"3.6.0","license":["Apache-2.0"]},{"version":"3.5.1","license":["MIT"]}]""", minified: false);

        var answer = await Read(handler);

        Assert.Equal(("MIT", (string?)null), (answer.Expression, answer.Failure));
        Assert.Equal(["https://repo.packagist.org/p2/monolog/monolog.json"], handler.Requests.Select(url => url.AbsoluteUri));
    }

    [Fact]
    public async Task Read_NameInCapitals_IsAskedInLowerCase()
    {
        var handler = Metadata("""[{"version":"3.5.1","license":["MIT"]}]""");

        await Read(handler, "Monolog/Monolog");

        Assert.Equal("https://repo.packagist.org/p2/monolog/monolog.json", handler.Requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task Read_SeveralLicenses_AreAChoice()
    {
        var answer = await Read(Metadata("""[{"version":"3.5.1","license":["GPL-2.0-only","MIT"]}]"""));

        Assert.Equal("(GPL-2.0-only) OR (MIT)", answer.Expression);
    }

    [Fact]
    public async Task Read_VersionWithALeadingV_IsTheSameVersion()
    {
        var answer = await Read(Metadata("""[{"version":"v3.5.1","license":["BSD-3-Clause"]}]"""));

        Assert.Equal("BSD-3-Clause", answer.Expression);
    }

    [Fact]
    public async Task Read_MinifiedEntryWithoutTheField_KeepsTheLicenseOfTheEntryBefore()
    {
        var answer = await Read(Metadata("""[{"version":"3.6.0","license":["MIT"]},{"version":"3.5.1"}]"""));

        Assert.Equal("MIT", answer.Expression);
    }

    [Fact]
    public async Task Read_MinifiedEntryThatUnsetsTheField_HasNoLicense()
    {
        var answer = await Read(Metadata("""[{"version":"3.6.0","license":["MIT"]},{"version":"3.5.1","license":"__unset"}]"""));

        Assert.Null(answer.Expression);
        Assert.Equal("The Packagist registry declares no license expression for monolog/monolog 3.5.1.", answer.Failure);
    }

    [Fact]
    public async Task Read_UnminifiedEntryWithoutTheField_DoesNotInheritALicense()
    {
        var answer = await Read(Metadata("""[{"version":"3.6.0","license":["MIT"]},{"version":"3.5.1"}]""", minified: false));

        Assert.Null(answer.Expression);
        Assert.Contains("declares no license expression", answer.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""[""]""")]
    public async Task Read_EmptyLicense_IsAFailureThatSaysSo(string license)
    {
        var answer = await Read(Metadata($$"""[{"version":"3.5.1","license":{{license}}}]"""));

        Assert.Null(answer.Expression);
        Assert.Equal("The Packagist registry declares no license expression for monolog/monolog 3.5.1.", answer.Failure);
    }

    [Fact]
    public async Task Read_EntryThatIsNotAnSpdxExpression_IsRefusedWithWhatTheRegistrySaid()
    {
        var answer = await Read(Metadata("""[{"version":"3.5.1","license":["MIT","proprietary licence, see file"]}]"""));

        Assert.Null(answer.Expression);
        Assert.Equal(
            "The Packagist registry declares 'proprietary licence, see file' for monolog/monolog 3.5.1, which is not an SPDX license expression.",
            answer.Failure);
    }

    [Fact]
    public async Task Read_VersionNotListed_IsAFailure()
    {
        var answer = await Read(Metadata("""[{"version":"3.6.0","license":["MIT"]}]"""));

        Assert.Null(answer.Expression);
        Assert.Equal("The Packagist registry has no version 3.5.1 of monolog/monolog.", answer.Failure);
    }

    [Fact]
    public async Task Read_PackageNotInTheAnswer_IsAFailure()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"packages":{"other/pkg":[]}}""", Encoding.UTF8, "application/json")
        });

        var answer = await Read(handler);

        Assert.Equal("The Packagist registry lists no version of monolog/monolog.", answer.Failure);
    }

    [Fact]
    public async Task Read_UnknownPackage_IsAFailure()
    {
        var answer = await Read(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(answer.Expression);
        Assert.Equal("The Packagist registry has no package monolog/monolog.", answer.Failure);
    }

    [Fact]
    public async Task Read_ServerError_IsAFailureNotAnEmptyLicense()
    {
        var answer = await Read(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)));

        Assert.Null(answer.Expression);
        Assert.Equal("The Packagist registry answered 502 for monolog/monolog 3.5.1.", answer.Failure);
    }

    [Fact]
    public async Task Read_Timeout_IsAFailureWithItsReason()
    {
        var answer = await Read(new ScriptedHandler(_ => throw new TaskCanceledException("timeout")));

        Assert.Null(answer.Expression);
        Assert.Equal("The Packagist registry did not answer for monolog/monolog 3.5.1 (TaskCanceledException).", answer.Failure);
    }

    [Fact]
    public async Task Read_NetworkFailure_IsAFailureWithItsReason()
    {
        var answer = await Read(new ScriptedHandler(_ => throw new HttpRequestException("refused")));

        Assert.Equal("The Packagist registry did not answer for monolog/monolog 3.5.1 (HttpRequestException).", answer.Failure);
    }

    [Theory]
    [InlineData("monolog", "3.5.1")]
    [InlineData("../evil/x", "3.5.1")]
    [InlineData("monolog/monolog", "dev-main")]
    [InlineData("monolog/monolog", "3.5.1/../x")]
    [InlineData("", "3.5.1")]
    public async Task Read_NameOrVersionThatIsNotPackagists_IsRefusedWithoutAnyRequest(string name, string version)
    {
        var handler = Metadata("""[{"version":"3.5.1","license":["MIT"]}]""");

        var answer = await Read(handler, name, version);

        Assert.Null(answer.Expression);
        Assert.NotNull(answer.Failure);
        Assert.Empty(handler.Requests);
    }
}
