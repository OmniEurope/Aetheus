// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Aetheus.Back.Components.AppMonitoring;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary> R-29: a probe failure is stored as a stable reason code with its host, never as the raw exception message. </summary>
public sealed class AppHttpProberFailureTests
{
    private const string Url = "https://probe.example:5301/health";

    private static async Task<AppProbeResultDto> ProbeAsync(HttpMessageHandler handler, int statusExpected = 200)
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(AppHttpProber.HttpClientName).Returns(new HttpClient(handler));
        var prober = new AppHttpProber(http, new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)));
        return await prober.ProbeAsync(1, Url, statusExpected, 1, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ForbiddenTarget_IsStoredAsItsCode_NotItsEnglishMessage()
    {
        var result = await ProbeAsync(new Throwing(new ProbeTargetForbiddenException()));

        Assert.False(result.IsUp);
        Assert.Equal("probe:forbidden_target;host=probe.example:5301", result.Error);
    }

    [Fact]
    public async Task NameResolutionFailure_IsStoredAsDns_NotTheOperatingSystemText()
    {
        var result = await ProbeAsync(new Throwing(new HttpRequestException(HttpRequestError.NameResolutionError, "Hôte inconnu.")));

        Assert.Equal("probe:dns;host=probe.example:5301", result.Error);
    }

    [Fact]
    public async Task ConnectionRefused_IsStoredAsRefused()
    {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "x", new SocketException((int)SocketError.ConnectionRefused));

        var result = await ProbeAsync(new Throwing(refused));

        Assert.Equal("probe:refused;host=probe.example:5301", result.Error);
    }

    [Fact]
    public async Task TlsFailure_IsStoredAsTls()
    {
        var tls = new HttpRequestException(HttpRequestError.SecureConnectionError, "x", new AuthenticationException("bad certificate"));

        var result = await ProbeAsync(new Throwing(tls));

        Assert.Equal("probe:tls;host=probe.example:5301", result.Error);
    }

    [Fact]
    public async Task AnUnknownFailure_FallsBackToUnreachableWithTheHost_NotTheMessage()
    {
        var result = await ProbeAsync(new Throwing(new HttpRequestException("secret internal detail")));

        Assert.Equal("probe:unreachable;host=probe.example:5301", result.Error);
    }

    [Fact]
    public async Task UnexpectedStatus_KeepsTheStatusCodes()
    {
        var result = await ProbeAsync(new Status(HttpStatusCode.InternalServerError));

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("probe:status;host=probe.example:5301;status=500;expected=200", result.Error);
    }

    [Fact]
    public async Task ATimeout_IsStoredAsTimeout()
    {
        var result = await ProbeAsync(new Hanging());

        Assert.Equal("probe:timeout;host=probe.example:5301", result.Error);
    }

    [Fact]
    public void TheStoredText_ParsesBack_AndAnOldTextDoesNot()
    {
        var parsed = AppProbeFailure.TryParse("probe:status;host=a.example;status=503;expected=200");

        Assert.Equal(new AppProbeFailure("status", "a.example", 503, 200), parsed);
        Assert.Null(AppProbeFailure.TryParse("Probe timed out"));
        Assert.Null(AppProbeFailure.TryParse("probe:unknown_code"));
    }

    private sealed class Throwing(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(error);
    }

    private sealed class Status(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(code));
    }

    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
