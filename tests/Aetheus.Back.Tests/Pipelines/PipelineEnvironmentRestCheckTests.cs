// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// A required REST callback check of a stage's environment: the host is resolved through an injected
/// resolver (no DNS leaves the test) and every answer must be public before the callback is called; a
/// callback that fails at the network level fails the check.
/// </summary>
public class PipelineEnvironmentRestCheckTests
{
    private static readonly PipelineStageDefinition Deploy = new() { Name = "Deploy", Environment = "prod" };
    private readonly IPipelineEnvironmentReader _reader = Substitute.For<IPipelineEnvironmentReader>();
    private readonly IHttpClientFactory _http = Substitute.For<IHttpClientFactory>();
    private readonly List<string> _called = [];

    public PipelineEnvironmentRestCheckTests()
    {
        _reader.FindEnvironmentByNameAsync("prod", 3, Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment { Id = 11, Name = "prod" });
    }

    private void Check(string configuration) =>
        _reader.GetEnvironmentChecksAsync(11, Arg.Any<CancellationToken>())
            .Returns([new EnvironmentCheck { Name = "ready", Type = EnvironmentCheckType.RestCallback, Configuration = configuration, TimeoutSeconds = 30 }]);

    private void Answer(HttpMessageHandler handler) =>
        _http.CreateClient(PipelinesModuleExtensions.EnvironmentCheckHttpClient).Returns(new HttpClient(handler));

    private PipelineEnvironmentCheckGuard Guard(params string[] addresses) =>
        new(_reader, _http, NullLogger<PipelineEnvironmentCheckGuard>.Instance,
            (host, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray()));

    [Fact]
    public async Task AHostResolvingToPublicAddresses_IsCalled_AndItsAnswerDecides()
    {
        Check("""{"url":"https://ready.example.com/health"}""");
        Answer(new RecordingHandler(_called, HttpStatusCode.OK));

        Assert.True(await Guard("93.184.216.34").CheckEnvironmentChecksAsync(Deploy, 3, TestContext.Current.CancellationToken));
        Assert.Equal(["https://ready.example.com/health"], _called);
    }

    [Fact]
    public async Task AFailingAnswer_FailsTheCheck()
    {
        Check("""{"url":"https://ready.example.com/health"}""");
        Answer(new RecordingHandler(_called, HttpStatusCode.ServiceUnavailable));

        Assert.False(await Guard("93.184.216.34").CheckEnvironmentChecksAsync(Deploy, 3, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("10.0.0.4")]
    [InlineData("93.184.216.34,169.254.169.254")]
    [InlineData("")]
    public async Task AHostWithAnInternalOrNoAddress_IsNeverCalled(string addresses)
    {
        Check("""{"url":"https://ready.example.com/health"}""");
        Answer(new RecordingHandler(_called, HttpStatusCode.OK));

        Assert.False(await Guard(addresses.Split(',', StringSplitOptions.RemoveEmptyEntries)).CheckEnvironmentChecksAsync(Deploy, 3, TestContext.Current.CancellationToken));
        Assert.Empty(_called);
    }

    [Fact]
    public async Task AHostThatDoesNotResolve_FailsTheCheck()
    {
        Check("""{"url":"https://missing.example.com/health"}""");
        var guard = new PipelineEnvironmentCheckGuard(_reader, _http, NullLogger<PipelineEnvironmentCheckGuard>.Instance,
            (_, _) => throw new SocketException((int)SocketError.HostNotFound));

        Assert.False(await guard.CheckEnvironmentChecksAsync(Deploy, 3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANetworkFailureOfAnAddressLiteral_FailsTheCheckWithoutResolving()
    {
        Check("""{"url":"http://93.184.216.34/health"}""");
        Answer(new ThrowingHandler());
        var resolved = false;
        var guard = new PipelineEnvironmentCheckGuard(_reader, _http, NullLogger<PipelineEnvironmentCheckGuard>.Instance,
            (_, _) => { resolved = true; return Task.FromResult(Array.Empty<IPAddress>()); });

        Assert.False(await guard.CheckEnvironmentChecksAsync(Deploy, 3, TestContext.Current.CancellationToken));
        Assert.False(resolved);
    }

    [Theory]
    [InlineData("""not json""")]
    [InlineData("""{"other":"x"}""")]
    [InlineData("""{"url":"ftp://ready.example.com/"}""")]
    [InlineData("""{"url":"http://localhost/health"}""")]
    public async Task AnUnusableConfiguration_FailsTheCheckWithoutACall(string configuration)
    {
        Check(configuration);
        Answer(new RecordingHandler(_called, HttpStatusCode.OK));

        Assert.False(await Guard("93.184.216.34").CheckEnvironmentChecksAsync(Deploy, 3, TestContext.Current.CancellationToken));
        Assert.Empty(_called);
    }

    private sealed class RecordingHandler(List<string> called, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            called.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
